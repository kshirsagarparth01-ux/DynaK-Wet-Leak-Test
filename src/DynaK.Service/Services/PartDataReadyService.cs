using System.Globalization;
using System.Text.Json;
using DynaK.Service.Configuration;
using DynaK.Service.Data;
using DynaK.Service.Models;
using DynaK.Service.Plc;

namespace DynaK.Service.Services;

public sealed class PartDataReadyService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ProductionRepository _records;
    private readonly EventRepository _events;
    private readonly AcquisitionState _state;
    private readonly PlcHandshakeService _handshake;
    private readonly LiveLeakValueFileWriter _liveLeakValueFile;
    private readonly IPlcClient _plcClient;
    private readonly ILogger<PartDataReadyService> _logger;
    private readonly SemaphoreSlim _processGate = new(1, 1);

    private PartDataReadyState _triggerState = PartDataReadyState.ARMED;
    private PendingPartData? _pending;
    private DateTimeOffset _nextRetryAt;
    private DateTimeOffset _nextExistingAcknowledgementRetryAt;
    private string? _observedSerialNumber;
    private bool? _observedSerialAlreadySaved;
    private bool _observedExistingPartAcknowledged;
    private string? _pendingSerialNumber;
    private string? _lastLoggedSerialNumberInput;
    private string? _lastLoggedReadyInput;
    private string? _lastLoggedSaveDecision;

    public PartDataReadyService(
        ProductionRepository records,
        EventRepository events,
        AcquisitionState state,
        PlcHandshakeService handshake,
        LiveLeakValueFileWriter liveLeakValueFile,
        IPlcClient plcClient,
        ILogger<PartDataReadyService> logger)
    {
        _records = records;
        _events = events;
        _state = state;
        _handshake = handshake;
        _liveLeakValueFile = liveLeakValueFile;
        _plcClient = plcClient;
        _logger = logger;
    }

    public PartDataReadyState State => _triggerState;

    public void ResetSession()
    {
        _triggerState = PartDataReadyState.ARMED;
        _pending = null;
        _nextRetryAt = default;
        _nextExistingAcknowledgementRetryAt = default;
        _observedSerialNumber = null;
        _observedSerialAlreadySaved = null;
        _observedExistingPartAcknowledged = false;
        _pendingSerialNumber = null;
        _lastLoggedSerialNumberInput = null;
        _lastLoggedReadyInput = null;
        _lastLoggedSaveDecision = null;
    }

    public async Task EndSessionAsync(CancellationToken cancellationToken)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            ResetSession();
        }
        finally
        {
            _processGate.Release();
        }
    }

    public async Task ProcessPollAsync(
        AppSettings settings,
        IReadOnlyDictionary<string, PlcSignalValue> signals,
        CancellationToken cancellationToken)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            await ProcessPollCoreAsync(settings, signals, cancellationToken);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task ProcessPollCoreAsync(
        AppSettings settings,
        IReadOnlyDictionary<string, PlcSignalValue> signals,
        CancellationToken cancellationToken)
    {
        var serialRaw = ReadRawSignal(signals, PlcSignalMapping.SerialNumberSignalName);
        var currentSerial = ReadSerialNumber(signals);
        var readyHigh = ReadPartDataReady(signals);
        var readyRaw = ReadRawSignal(signals, PlcSignalMapping.PartDataReadySignalName);
        LogLiveInputs(serialRaw, currentSerial, readyRaw, readyHigh);

        await TryCompletePendingAsync(settings, cancellationToken);
        if (_pending is not null)
        {
            LogSaveDecision(false, readyHigh, false, true, "a captured snapshot is still awaiting a successful database insert or post-save processing");
            return;
        }

        var serialChanged = !StringComparer.Ordinal.Equals(_observedSerialNumber, currentSerial);
        if (serialChanged)
        {
            _observedSerialNumber = currentSerial;
            _observedSerialAlreadySaved = null;
            _observedExistingPartAcknowledged = false;
            _nextExistingAcknowledgementRetryAt = default;
            _pendingSerialNumber = null;
            _lastLoggedSaveDecision = null;

            if (IsValidSerialNumber(currentSerial))
            {
                _logger.LogInformation("New Serial Number detected: {SerialNumber}", currentSerial);
            }
        }

        if (!IsValidSerialNumber(currentSerial))
        {
            _triggerState = PartDataReadyState.ARMED;
            LogSaveDecision(false, readyHigh, false, true, "Serial Number is empty or zero");
            return;
        }

        var databaseReady = true;
        if (_observedSerialAlreadySaved is null)
        {
            try
            {
                _observedSerialAlreadySaved = await IsAlreadySavedAsync(settings.StationId, currentSerial, cancellationToken);
                if (_observedSerialAlreadySaved == false)
                {
                    _pendingSerialNumber = currentSerial;
                    _logger.LogInformation("Pending Serial Number: {SerialNumber}", _pendingSerialNumber);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                databaseReady = false;
                _logger.LogError(ex, "Serial {SerialNumber} duplicate check failed: {Message}. Will retry on the next PLC poll.", currentSerial, ex.Message);
                await _events.StartActiveAsync(settings.StationId, "DATABASE_PERSISTENCE_ERROR", "ERROR", ex.GetType().Name, ex.Message, DateTimeOffset.Now, cancellationToken);
            }
        }

        var alreadySaved = _observedSerialAlreadySaved == true;
        if (alreadySaved)
        {
            if (readyHigh == true)
            {
                await TryAcknowledgeExistingSavedPartAsync(settings, currentSerial, cancellationToken);
            }

            if (readyHigh != true || _observedExistingPartAcknowledged)
            {
                _triggerState = PartDataReadyState.ARMED;
            }

            LogSaveDecision(
                false,
                readyHigh,
                true,
                databaseReady,
                readyHigh != true
                    ? "D1075 is not HIGH"
                    : _observedExistingPartAcknowledged
                        ? "Serial Number already exists in History and its current held-HIGH event is already acknowledged"
                        : "Serial Number already exists in History and DATA SAVED acknowledgement is pending retry");
            return;
        }

        var saveConditionSatisfied = databaseReady && _pendingSerialNumber is not null && readyHigh == true;
        LogSaveDecision(
            saveConditionSatisfied,
            readyHigh,
            false,
            databaseReady,
            readyHigh != true
                ? "D1075 is not HIGH"
                : !databaseReady
                    ? "database duplicate check is unavailable"
                    : "no unsaved pending Serial Number is available");

        if (!saveConditionSatisfied)
        {
            _triggerState = PartDataReadyState.ARMED;
            return;
        }

        _logger.LogInformation("Reading production snapshot...");
        await CaptureSnapshotAsync(settings, _pendingSerialNumber!, cancellationToken);
        await TryCompletePendingAsync(settings, cancellationToken);
    }

    private async Task TryAcknowledgeExistingSavedPartAsync(
        AppSettings settings,
        string serialNumber,
        CancellationToken cancellationToken)
    {
        if (_observedExistingPartAcknowledged || DateTimeOffset.Now < _nextExistingAcknowledgementRetryAt)
        {
            return;
        }

        _triggerState = PartDataReadyState.SAVING;
        _logger.LogInformation(
            "D1075 remains HIGH for already-saved Serial {SerialNumber}; retrying DATA SAVED without inserting another History record.",
            serialNumber);

        var acknowledged = await _handshake.PulseDataSavedAsync(cancellationToken);
        if (acknowledged)
        {
            _observedExistingPartAcknowledged = true;
            _nextExistingAcknowledgementRetryAt = default;
            _triggerState = PartDataReadyState.ARMED;
            await _events.ClearActiveByTypeAsync(settings.StationId, "PLC_HANDSHAKE_ERROR", DateTimeOffset.Now, cancellationToken);
            _logger.LogInformation(
                "Already-saved Serial {SerialNumber} was acknowledged without creating another History record.",
                serialNumber);
            return;
        }

        _nextExistingAcknowledgementRetryAt = DateTimeOffset.Now.Add(RetryDelay);
        await _events.StartActiveAsync(
            settings.StationId,
            "PLC_HANDSHAKE_ERROR",
            "ERROR",
            "DATA_SAVED_WRITE_FAILED",
            $"Serial {serialNumber} is already durable in local History, but DATA SAVED acknowledgement failed. The acknowledgement will retry without inserting another production record.",
            DateTimeOffset.Now,
            cancellationToken);

        _logger.LogWarning(
            "Serial {SerialNumber} is already saved, but DATA SAVED acknowledgement failed. Retrying later without another database insert.",
            serialNumber);
    }

    private async Task CaptureSnapshotAsync(AppSettings settings, string polledSerialNumber, CancellationToken cancellationToken)
    {
        _triggerState = PartDataReadyState.CAPTURING_SNAPSHOT;
        try
        {
            var snapshot = await _plcClient.ReadPartDataSnapshotAsync(cancellationToken);
            if (snapshot is null)
            {
                await ReportSnapshotFailureAsync(
                    settings,
                    "PART_DATA_SNAPSHOT_INCOMPLETE",
                    $"D1075=1, Serial {polledSerialNumber} snapshot was incomplete. No History record was created. Will retry while trigger remains HIGH.",
                    null,
                    cancellationToken);
                return;
            }

            var snapshotSerialNumber = NormalizeSerialNumber(snapshot.SerialNumber);
            if (!IsValidSerialNumber(snapshotSerialNumber))
            {
                await ReportSnapshotFailureAsync(
                    settings,
                    "SERIAL_NUMBER_INVALID",
                    "D1075=1, but the captured Serial Number is empty or zero. No production record was saved or acknowledged; capture will retry while the trigger remains HIGH.",
                    null,
                    cancellationToken);
                return;
            }

            var snapshotModelNumber = NormalizeModelNumber(snapshot.ModelNumber);
            if (!IsValidModelNumber(snapshotModelNumber))
            {
                await ReportSnapshotFailureAsync(
                    settings,
                    "MODEL_NUMBER_INVALID",
                    $"D1075=1, Serial {snapshotSerialNumber} has an empty or zero Model Number. No History record was created.",
                    null,
                    cancellationToken);
                return;
            }

            if (!StringComparer.Ordinal.Equals(snapshotSerialNumber, polledSerialNumber))
            {
                _logger.LogWarning(
                    "Serial Number changed during snapshot capture from {PolledSerialNumber} to {SnapshotSerialNumber}; the complete current snapshot will be evaluated.",
                    polledSerialNumber,
                    snapshotSerialNumber);

                _observedSerialNumber = snapshotSerialNumber;
                _observedSerialAlreadySaved = null;
                _pendingSerialNumber = null;
                _logger.LogInformation("New Serial Number detected: {SerialNumber}", snapshotSerialNumber);

                if (await IsAlreadySavedAsync(settings.StationId, snapshotSerialNumber, cancellationToken))
                {
                    MarkSerialSaved(snapshotSerialNumber);
                    _triggerState = PartDataReadyState.ARMED;
                    return;
                }

                _observedSerialAlreadySaved = false;
                _pendingSerialNumber = snapshotSerialNumber;
                _logger.LogInformation("Pending Serial Number: {SerialNumber}", snapshotSerialNumber);
            }

            var snapshotValues = JsonSerializer.Serialize(snapshot.Signals, SnapshotJsonOptions);
            _logger.LogInformation("Snapshot: {SnapshotValues}", snapshotValues);
            _logger.LogInformation(
                "Snapshot captured: Serial={SerialNumber}, Model={ModelNumber}, Leak={LeakValue} {LeakUnit}, Result={Result}, Mode={Mode}, Error={Error}, ConfiguredSignals={SignalCount}.",
                snapshotSerialNumber,
                snapshotModelNumber,
                snapshot.LeakTestValue,
                snapshot.LeakTestUnit,
                snapshot.ResolvedResult,
                snapshot.ResolvedMode,
                snapshot.ErrorDescription,
                snapshot.Signals.Count);

            var record = await BuildRecordAsync(
                settings,
                snapshot with { SerialNumber = snapshotSerialNumber, ModelNumber = snapshotModelNumber },
                cancellationToken);

            _pending = new PendingPartData(record, false, false);
            _triggerState = PartDataReadyState.SAVING;
            _nextRetryAt = default;
            await _events.ClearActiveByTypeAsync(settings.StationId, "PART_DATA_SNAPSHOT_ERROR", DateTimeOffset.Now, cancellationToken);
            _logger.LogInformation("Saving Serial {SerialNumber}, Model {ModelNumber}.", record.SerialNumber, record.ModelNumber);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await ReportSnapshotFailureAsync(settings, ex.GetType().Name, ex.Message, ex, cancellationToken);
        }
    }

    private async Task<bool> IsAlreadySavedAsync(string stationId, string serialNumber, CancellationToken cancellationToken)
    {
        var normalized = NormalizeSerialNumber(serialNumber);
        var alreadySaved = await _records.ExistsBySerialNumberAsync(stationId, normalized, cancellationToken);
        _logger.LogInformation("Serial already exists: {AlreadySaved}", alreadySaved);
        if (alreadySaved)
        {
            _logger.LogInformation("Serial {SerialNumber} already saved - skipping duplicate.", normalized);
        }

        return alreadySaved;
    }

    private async Task<ProductionRecord> BuildRecordAsync(
        AppSettings settings,
        PartDataSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var window = new ShiftResolver(settings.Shifts).ResolveWindow(snapshot.Timestamp);
        var counters = await _records.GetCountersForWindowAsync(
            settings.StationId,
            window.Shift.Name,
            window.StartsAt,
            window.EndsAt,
            cancellationToken);
        var capturedAt = DateTimeOffset.Now;
        var sequenceId = await _records.GetNextSequenceIdAsync(settings.StationId, cancellationToken);
        var errorCode = IsZero(snapshot.ErrorCode) || IsNoError(snapshot.ErrorDescription)
            ? null
            : snapshot.ErrorCode;

        return new ProductionRecord(
            0,
            settings.StationId,
            sequenceId,
            NormalizeSerialNumber(snapshot.SerialNumber),
            NormalizeModelNumber(snapshot.ModelNumber),
            DateOnly.FromDateTime(snapshot.Timestamp.DateTime),
            TimeOnly.FromDateTime(snapshot.Timestamp.DateTime),
            snapshot.Timestamp,
            window.Shift.Name,
            snapshot.TargetPartsPerShift ?? 0,
            snapshot.ActualPartCount ?? counters.Actual + 1,
            counters.Ok + (IsResult(snapshot.ResolvedResult, ProductionResult.OK) ? 1 : 0),
            counters.Ng + (IsResult(snapshot.ResolvedResult, ProductionResult.NG) ? 1 : 0),
            counters.Rework + (IsResult(snapshot.ResolvedResult, ProductionResult.REWORK) ? 1 : 0),
            snapshot.LeakTestValue,
            snapshot.LeakTestUnit,
            snapshot.LowerLimit,
            snapshot.UpperLimit,
            snapshot.RawResultValue,
            snapshot.ResolvedResult,
            snapshot.RawModeValue,
            snapshot.ResolvedMode,
            snapshot.IsAutoMode,
            snapshot.IsMachineRunning,
            errorCode,
            errorCode is null ? null : snapshot.ErrorDescription,
            snapshot.RawRunningStatusValue,
            snapshot.ResolvedRunningStatus,
            JsonSerializer.Serialize(snapshot.Signals, SnapshotJsonOptions),
            capturedAt);
    }

    private async Task TryCompletePendingAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_pending is null || DateTimeOffset.Now < _nextRetryAt)
        {
            return;
        }

        if (!_pending.DatabaseComplete)
        {
            var pendingRecord = _pending.Record;
            try
            {
                _logger.LogInformation("Attempting database insert...");
                if (await IsAlreadySavedAsync(pendingRecord.StationId, pendingRecord.SerialNumber, cancellationToken))
                {
                    MarkSerialSaved(pendingRecord.SerialNumber);
                    _pending = null;
                    _nextRetryAt = default;
                    _triggerState = PartDataReadyState.ARMED;
                    return;
                }

                var insert = await _records.InsertAsync(pendingRecord, cancellationToken);
                ProductionRecord persisted;
                if (insert.Duplicate)
                {
                    persisted = await _records.GetBySequenceIdAsync(
                        pendingRecord.StationId,
                        pendingRecord.PlcSequenceId,
                        cancellationToken)
                        ?? throw new InvalidOperationException("Duplicate Part Data Ready sequence was reported but the existing row could not be loaded.");
                    _logger.LogWarning("Part Data Ready sequence {SequenceId} was already persisted; no duplicate History row was created.", persisted.PlcSequenceId);
                }
                else if (insert.Inserted && insert.Id.HasValue)
                {
                    persisted = pendingRecord with { Id = insert.Id.Value };
                    _logger.LogInformation("Database insert successful: {RecordId}", persisted.Id);
                    _logger.LogInformation(
                        "Complete Part History record {RecordId} saved for Part Data Ready sequence {SequenceId} ({SerialNumber}).",
                        persisted.Id,
                        persisted.PlcSequenceId,
                        persisted.SerialNumber);
                }
                else
                {
                    await ReportDatabaseFailureAsync(pendingRecord, "INSERT_FAILED", "Complete Part History insert did not return a durable row id.", null, cancellationToken);
                    return;
                }

                _pending = _pending with { Record = persisted, DatabaseComplete = true };
                MarkSerialSaved(persisted.SerialNumber);
                _state.Update(persisted);
                _nextRetryAt = default;
                await _events.ClearActiveByTypeAsync(settings.StationId, "DATABASE_PERSISTENCE_ERROR", DateTimeOffset.Now, cancellationToken);
                _logger.LogInformation("Serial {SerialNumber} saved successfully as History record {RecordId}.", persisted.SerialNumber, persisted.Id);
            }
            catch (Exception ex)
            {
                await ReportDatabaseFailureAsync(pendingRecord, ex.GetType().Name, ex.Message, ex, cancellationToken);
                return;
            }
        }

        if (!_pending.LiveLeakValueFileComplete && _pending.Record.LeakTestValue is null)
        {
            _logger.LogWarning(
                "Live leak-value text file was not updated for {SerialNumber} because the captured PLC value was unavailable.",
                _pending.Record.SerialNumber);
            _pending = _pending with { LiveLeakValueFileComplete = true };
        }

        if (!_pending.LiveLeakValueFileComplete)
        {
            try
            {
                var updated = await _liveLeakValueFile.WriteIfChangedAsync(
                    settings.LiveLeakValueFilePath,
                    _pending.Record.LeakTestValue!.Value,
                    cancellationToken);
                _pending = _pending with { LiveLeakValueFileComplete = true };
                await _events.ClearActiveByTypeAsync(settings.StationId, "LIVE_LEAK_FILE_ERROR", DateTimeOffset.Now, cancellationToken);
                if (updated)
                {
                    _logger.LogInformation(
                        "Live text file updated for {SerialNumber} at {Path}: {LeakValue}",
                        _pending.Record.SerialNumber,
                        StationDataPaths.ResolveMachinePath(settings.LiveLeakValueFilePath),
                        _pending.Record.LeakTestValue.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _logger.LogError(ex, "Live leak value text-file update failed for {SerialNumber}", _pending.Record.SerialNumber);
                await _events.StartActiveAsync(settings.StationId, "LIVE_LEAK_FILE_ERROR", "ERROR", ex.GetType().Name, ex.Message, DateTimeOffset.Now, cancellationToken);
                ScheduleRetry();
                return;
            }
        }

        await TrackFaultStateAsync(
            _pending.Record,
            cancellationToken);

        var dataSavedAcknowledged =
            await _handshake.PulseDataSavedAsync(cancellationToken);

        if (!dataSavedAcknowledged)
        {
            await _events.StartActiveAsync(
                settings.StationId,
                "PLC_HANDSHAKE_ERROR",
                "ERROR",
                "DATA_SAVED_WRITE_FAILED",
                "The complete production record is durable in local History, but DATA SAVED acknowledgement failed. The acknowledgement will retry without inserting another production record.",
                DateTimeOffset.Now,
                cancellationToken);

            ScheduleRetry();

            _logger.LogWarning(
                "Serial {SerialNumber} is durable in local History, but DATA SAVED acknowledgement failed. The saved record remains pending for acknowledgement retry.",
                _pending.Record.SerialNumber);

            return;
        }

        await _events.ClearActiveByTypeAsync(
            settings.StationId,
            "PLC_HANDSHAKE_ERROR",
            DateTimeOffset.Now,
            cancellationToken);

        var savedPart = _pending.Record;

        _pending = null;
        _nextRetryAt = default;
        _triggerState = PartDataReadyState.ARMED;

        if (StringComparer.Ordinal.Equals(
                _observedSerialNumber,
                NormalizeSerialNumber(savedPart.SerialNumber)))
        {
            _observedExistingPartAcknowledged = true;
            _nextExistingAcknowledgementRetryAt = default;
        }

        _logger.LogInformation(
            "Serial {SerialNumber} saved and acknowledged.",
            savedPart.SerialNumber);
    }

    private async Task ReportSnapshotFailureAsync(
        AppSettings settings,
        string code,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        if (exception is null)
        {
            _logger.LogError("Part-data snapshot capture failed: {Message}", message);
        }
        else
        {
            _logger.LogError(exception, "Part-data snapshot capture failed while D1075=1. Will retry while trigger remains HIGH.");
        }

        await _events.StartActiveAsync(
            settings.StationId,
            "PART_DATA_SNAPSHOT_ERROR",
            "ERROR",
            code,
            message,
            DateTimeOffset.Now,
            cancellationToken);
    }

    private async Task ReportDatabaseFailureAsync(
        ProductionRecord record,
        string code,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        if (exception is null)
        {
            _logger.LogError("D1075=1, Serial {SerialNumber} save failed: {Message}. Will retry while trigger remains HIGH.", record.SerialNumber, message);
        }
        else
        {
            _logger.LogError(exception, "D1075=1, Serial {SerialNumber} save failed: {Message}. Will retry while trigger remains HIGH.", record.SerialNumber, message);
        }

        await _events.StartActiveAsync(record.StationId, "DATABASE_PERSISTENCE_ERROR", "ERROR", code, message, DateTimeOffset.Now, cancellationToken);
        _nextRetryAt = default;
        _triggerState = PartDataReadyState.SAVING;
    }

    private async Task TrackFaultStateAsync(ProductionRecord record, CancellationToken cancellationToken)
    {
        if (record.ErrorCode is { Length: > 0 } code && !IsZero(code) && !IsNoError(record.ErrorDescription))
        {
            await _events.StartActiveAsync(record.StationId, "MACHINE_FAULT", "ERROR", code, record.ErrorDescription ?? code, record.Timestamp, cancellationToken);
            return;
        }

        await _events.ClearActiveByTypeAsync(record.StationId, "MACHINE_FAULT", record.Timestamp, cancellationToken);
    }

    private void ScheduleRetry()
    {
        _nextRetryAt = DateTimeOffset.Now.Add(RetryDelay);
        _triggerState = PartDataReadyState.SAVING;
    }

    private void MarkSerialSaved(string serialNumber)
    {
        var normalized = NormalizeSerialNumber(serialNumber);
        if (StringComparer.Ordinal.Equals(_observedSerialNumber, normalized))
        {
            _observedSerialAlreadySaved = true;
        }

        if (StringComparer.Ordinal.Equals(_pendingSerialNumber, normalized))
        {
            _pendingSerialNumber = null;
        }
    }

    private void LogLiveInputs(string serialNumberRaw, string serialNumber, string readyRaw, bool? readyHigh)
    {
        var serialInput = $"{serialNumberRaw}|{serialNumber}";
        if (!StringComparer.Ordinal.Equals(_lastLoggedSerialNumberInput, serialInput))
        {
            _lastLoggedSerialNumberInput = serialInput;
            _logger.LogInformation("Serial Number raw value received: {RawValue}", serialNumberRaw);
            _logger.LogInformation("Decoded Serial Number: {SerialNumber}", serialNumber.Length == 0 ? "<empty>" : serialNumber);
        }

        var decodedReady = readyHigh.HasValue ? (readyHigh.Value ? "1" : "0") : "<unavailable>";
        var readyInput = $"{readyRaw}|{decodedReady}";
        if (!StringComparer.Ordinal.Equals(_lastLoggedReadyInput, readyInput))
        {
            _lastLoggedReadyInput = readyInput;
            _logger.LogInformation("D1075 raw value: {RawValue}", readyRaw);
            _logger.LogInformation("D1075 decoded value: {DecodedValue}", decodedReady);
        }
    }

    private void LogSaveDecision(
        bool saveConditionSatisfied,
        bool? readyHigh,
        bool alreadySaved,
        bool databaseReady,
        string reason)
    {
        var pendingSerialNumber = _pendingSerialNumber ?? _pending?.Record.SerialNumber ?? "<none>";
        var decodedReady = readyHigh.HasValue ? (readyHigh.Value ? "1" : "0") : "<unavailable>";
        var signature = $"{pendingSerialNumber}|{decodedReady}|{alreadySaved}|{databaseReady}|{saveConditionSatisfied}|{reason}";
        if (StringComparer.Ordinal.Equals(_lastLoggedSaveDecision, signature))
        {
            return;
        }

        _lastLoggedSaveDecision = signature;
        _logger.LogInformation("Save condition satisfied: {SaveConditionSatisfied}", saveConditionSatisfied);
        if (!saveConditionSatisfied)
        {
            _logger.LogInformation(
                "SAVE SKIPPED: pendingSerialNumber = {PendingSerialNumber}; D1075 = {D1075}; alreadySaved = {AlreadySaved}; databaseReady = {DatabaseReady}. Reason: {Reason}",
                pendingSerialNumber,
                decodedReady,
                alreadySaved,
                databaseReady,
                reason);
        }
    }

    private static string ReadRawSignal(IReadOnlyDictionary<string, PlcSignalValue> signals, string signalName)
    {
        if (!signals.TryGetValue(signalName, out var signal))
        {
            return "<missing>";
        }

        if (!string.IsNullOrWhiteSpace(signal.Error))
        {
            return $"<decode error: {signal.Error}>";
        }

        return Convert.ToString(signal.RawValue, CultureInfo.InvariantCulture)?.Trim() ?? "<null>";
    }

    private static bool? ReadPartDataReady(IReadOnlyDictionary<string, PlcSignalValue> signals)
    {
        if (!signals.TryGetValue(PlcSignalMapping.PartDataReadySignalName, out var signal) ||
            !string.IsNullOrWhiteSpace(signal.Error))
        {
            return null;
        }

        if (signal.ValueMapMatched && TryReadHighLow(signal.InterpretedValue, out var mapped))
        {
            return mapped;
        }

        if (TryReadHighLow(signal.RawValue, out var raw))
        {
            return raw;
        }

        return TryReadHighLow(signal.InterpretedValue, out var interpreted) ? interpreted : null;
    }

    private static string ReadSerialNumber(IReadOnlyDictionary<string, PlcSignalValue> signals)
    {
        if (!signals.TryGetValue(PlcSignalMapping.SerialNumberSignalName, out var signal) ||
            !string.IsNullOrWhiteSpace(signal.Error))
        {
            return "";
        }

        return NormalizeSerialNumber(Convert.ToString(signal.InterpretedValue ?? signal.RawValue, CultureInfo.InvariantCulture));
    }

    private static string NormalizeSerialNumber(string? value) => (value ?? "").Trim();

    private static string NormalizeModelNumber(string? value) => (value ?? "").Trim();

    private static bool IsValidSerialNumber(string value) =>
        value.Length > 0 &&
        (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var numeric) || numeric != 0m);

    private static bool IsValidModelNumber(string value) =>
        value.Length > 0 &&
        (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var numeric) || numeric != 0m);

    private static bool TryReadHighLow(object? value, out bool high)
    {
        if (value is bool boolean)
        {
            high = boolean;
            return true;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
        if (text.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("HIGH", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
        {
            high = true;
            return true;
        }

        if (text.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("LOW", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            high = false;
            return true;
        }

        high = false;
        return false;
    }

    private static bool IsResult(string value, ProductionResult expected)
    {
        var normalized = value.Trim().ToUpperInvariant();
        return expected switch
        {
            ProductionResult.OK => normalized == "OK",
            ProductionResult.NG => normalized == "NG" || normalized.StartsWith("NG ", StringComparison.Ordinal) || normalized.StartsWith("NG/", StringComparison.Ordinal),
            ProductionResult.REWORK => normalized == "REWORK",
            _ => false
        };
    }

    private static bool IsZero(string? value) =>
        value is not null &&
        (value.Equals("0", StringComparison.OrdinalIgnoreCase) || value.Equals("0.0", StringComparison.OrdinalIgnoreCase));

    private static bool IsNoError(string? value) =>
        value is not null && value.Equals("No Error", StringComparison.OrdinalIgnoreCase);
}

public enum PartDataReadyState
{
    ARMED,
    CAPTURING_SNAPSHOT,
    SAVING
}

internal sealed record PendingPartData(
    ProductionRecord Record,
    bool DatabaseComplete,
    bool LiveLeakValueFileComplete);
