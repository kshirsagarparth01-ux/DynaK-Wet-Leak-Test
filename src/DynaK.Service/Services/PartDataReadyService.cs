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
    private string? _observedPartNumber;
    private bool? _observedPartAlreadySaved;
    private string? _pendingPartNumber;
    private string? _lastLoggedPartNumberInput;
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
        _observedPartNumber = null;
        _observedPartAlreadySaved = null;
        _pendingPartNumber = null;
        _lastLoggedPartNumberInput = null;
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
        var partNumberRaw = ReadRawSignal(signals, "Part Number");
        var currentPartNumber = ReadPartNumber(signals);
        var readyHigh = ReadPartDataReady(signals);
        var readyRaw = ReadRawSignal(signals, PlcSignalMapping.PartDataReadySignalName);
        LogLiveInputs(partNumberRaw, currentPartNumber, readyRaw, readyHigh);

        await TryCompletePendingAsync(settings, cancellationToken);
        if (_pending is not null)
        {
            LogSaveDecision(false, readyHigh, false, true, "a captured snapshot is still awaiting a successful database insert or post-save processing");
            return;
        }

        var partNumberChanged = !StringComparer.Ordinal.Equals(_observedPartNumber, currentPartNumber);
        if (partNumberChanged)
        {
            _observedPartNumber = currentPartNumber;
            _observedPartAlreadySaved = null;
            _pendingPartNumber = null;
            _lastLoggedSaveDecision = null;
            if (IsValidPartNumber(currentPartNumber))
            {
                _logger.LogInformation("New Part Number detected: {PartNumber}", currentPartNumber);
            }
        }

        if (!IsValidPartNumber(currentPartNumber))
        {
            _triggerState = PartDataReadyState.ARMED;
            LogSaveDecision(false, readyHigh, false, true, "Part Number is empty or zero");
            return;
        }

        var databaseReady = true;
        if (_observedPartAlreadySaved is null)
        {
            try
            {
                _observedPartAlreadySaved = await IsAlreadySavedAsync(settings.StationId, currentPartNumber, cancellationToken);
                if (_observedPartAlreadySaved == false)
                {
                    _pendingPartNumber = currentPartNumber;
                    _logger.LogInformation("Pending Part Number: {PartNumber}", _pendingPartNumber);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                databaseReady = false;
                _logger.LogError(ex, "Part {PartNumber} duplicate check failed: {Message}. Will retry on the next PLC poll.", currentPartNumber, ex.Message);
                await _events.StartActiveAsync(settings.StationId, "DATABASE_PERSISTENCE_ERROR", "ERROR", ex.GetType().Name, ex.Message, DateTimeOffset.Now, cancellationToken);
            }
        }

        var alreadySaved = _observedPartAlreadySaved == true;
        var saveConditionSatisfied = databaseReady && !alreadySaved &&
            _pendingPartNumber is not null && readyHigh == true;
        LogSaveDecision(
            saveConditionSatisfied,
            readyHigh,
            alreadySaved,
            databaseReady,
            readyHigh != true ? "D1075 is not HIGH" : alreadySaved ? "Part Number already exists in History" : "no unsaved pending Part Number is available");

        if (!saveConditionSatisfied)
        {
            _triggerState = PartDataReadyState.ARMED;
            return;
        }

        _logger.LogInformation("Reading production snapshot...");
        await CaptureSnapshotAsync(settings, _pendingPartNumber!, cancellationToken);
        await TryCompletePendingAsync(settings, cancellationToken);
    }

    private async Task CaptureSnapshotAsync(AppSettings settings, string polledPartNumber, CancellationToken cancellationToken)
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
                    $"D1075=1, Part {polledPartNumber} snapshot was incomplete. No History record was created. Will retry while trigger remains HIGH.",
                    null,
                    cancellationToken);
                return;
            }

            var snapshotPartNumber = NormalizePartNumber(snapshot.PartNumber);
            if (!IsValidPartNumber(snapshotPartNumber))
            {
                await ReportSnapshotFailureAsync(
                    settings,
                    "PART_NUMBER_INVALID",
                    $"D1075=1, but the captured Part Number '{snapshotPartNumber}' is empty or zero. Will retry while trigger remains HIGH.",
                    null,
                    cancellationToken);
                return;
            }

            if (!StringComparer.Ordinal.Equals(snapshotPartNumber, polledPartNumber))
            {
                _logger.LogWarning(
                    "Part Number changed during snapshot capture from {PolledPartNumber} to {SnapshotPartNumber}; the complete current snapshot will be evaluated.",
                    polledPartNumber,
                    snapshotPartNumber);
                _observedPartNumber = snapshotPartNumber;
                _observedPartAlreadySaved = null;
                _pendingPartNumber = null;
                _logger.LogInformation("New Part Number detected: {PartNumber}", snapshotPartNumber);
                if (await IsAlreadySavedAsync(settings.StationId, snapshotPartNumber, cancellationToken))
                {
                    MarkPartSaved(snapshotPartNumber);
                    _triggerState = PartDataReadyState.ARMED;
                    return;
                }

                _observedPartAlreadySaved = false;
                _pendingPartNumber = snapshotPartNumber;
                _logger.LogInformation("Pending Part Number: {PartNumber}", snapshotPartNumber);
            }

            var snapshotValues = JsonSerializer.Serialize(snapshot.Signals, SnapshotJsonOptions);
            _logger.LogInformation("Snapshot: {SnapshotValues}", snapshotValues);
            _logger.LogInformation(
                "Snapshot captured for Part {PartNumber}: Serial={SerialNumber}, QR={QrCode}, Leak={LeakValue} {LeakUnit}, Result={Result}, Mode={Mode}, Error={Error}, ConfiguredSignals={SignalCount}.",
                snapshotPartNumber,
                snapshot.SerialNumber ?? "<empty>",
                snapshot.QrCode,
                snapshot.LeakTestValue,
                snapshot.LeakTestUnit,
                snapshot.ResolvedResult,
                snapshot.ResolvedMode,
                snapshot.ErrorDescription,
                snapshot.Signals.Count);
            var record = await BuildRecordAsync(settings, snapshot, cancellationToken);
            _pending = new PendingPartData(record, false, false);
            _triggerState = PartDataReadyState.SAVING;
            _nextRetryAt = default;
            await _events.ClearActiveByTypeAsync(settings.StationId, "PART_DATA_SNAPSHOT_ERROR", DateTimeOffset.Now, cancellationToken);
            _logger.LogInformation("Saving Part {PartNumber}.", record.PartNumber);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await ReportSnapshotFailureAsync(
                settings,
                ex.GetType().Name,
                ex.Message,
                ex,
                cancellationToken);
        }
    }

    private async Task<bool> IsAlreadySavedAsync(string stationId, string partNumber, CancellationToken cancellationToken)
    {
        var alreadySaved = await _records.ExistsByPartNumberAsync(stationId, partNumber, cancellationToken);
        _logger.LogInformation("Part already exists: {AlreadySaved}", alreadySaved);
        if (alreadySaved)
        {
            _logger.LogInformation("Part {PartNumber} already saved - skipping duplicate.", partNumber);
            return true;
        }

        return false;
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
            string.IsNullOrWhiteSpace(snapshot.SerialNumber) ? null : snapshot.SerialNumber.Trim(),
            snapshot.QrCode.Trim(),
            snapshot.PartNumber.Trim(),
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
                if (await IsAlreadySavedAsync(pendingRecord.StationId, pendingRecord.PartNumber, cancellationToken))
                {
                    MarkPartSaved(pendingRecord.PartNumber);
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
                        "Complete Part History record {RecordId} saved for Part Data Ready sequence {SequenceId} ({PartNumber}).",
                        persisted.Id,
                        persisted.PlcSequenceId,
                        persisted.PartNumber);
                }
                else
                {
                    await ReportDatabaseFailureAsync(pendingRecord, "INSERT_FAILED", "Complete Part History insert did not return a durable row id.", null, cancellationToken);
                    return;
                }

                _pending = _pending with { Record = persisted, DatabaseComplete = true };
                MarkPartSaved(persisted.PartNumber);
                _state.Update(persisted);
                _nextRetryAt = default;
                await _events.ClearActiveByTypeAsync(settings.StationId, "DATABASE_PERSISTENCE_ERROR", DateTimeOffset.Now, cancellationToken);
                _logger.LogInformation("Part {PartNumber} saved successfully as History record {RecordId}.", persisted.PartNumber, persisted.Id);
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
                "Live leak-value text file was not updated for {PartNumber} because the captured PLC value was unavailable.",
                _pending.Record.PartNumber);
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
                        "Live text file updated for {PartNumber} at {Path}: {LeakValue}",
                        _pending.Record.PartNumber,
                        StationDataPaths.ResolveMachinePath(settings.LiveLeakValueFilePath),
                        _pending.Record.LeakTestValue.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _logger.LogError(ex, "Live leak value text-file update failed for {PartNumber}", _pending.Record.PartNumber);
                await _events.StartActiveAsync(settings.StationId, "LIVE_LEAK_FILE_ERROR", "ERROR", ex.GetType().Name, ex.Message, DateTimeOffset.Now, cancellationToken);
                ScheduleRetry();
                return;
            }
        }

        var dataSavedAcknowledged = await _handshake.PulseDataSavedAsync(cancellationToken);
        if (dataSavedAcknowledged)
        {
            await _events.ClearActiveByTypeAsync(settings.StationId, "PLC_HANDSHAKE_ERROR", DateTimeOffset.Now, cancellationToken);
        }
        else
        {
            await _events.StartActiveAsync(
                settings.StationId,
                "PLC_HANDSHAKE_ERROR",
                "ERROR",
                "DATA_SAVED_WRITE_FAILED",
                "Configured DATA SAVED signal could not be written after the complete database record and live text-file update. Local History acquisition will continue.",
                DateTimeOffset.Now,
                cancellationToken);
        }

        await TrackFaultStateAsync(_pending.Record, cancellationToken);
        var savedPart = _pending.Record;
        _pending = null;
        _nextRetryAt = default;
        _triggerState = PartDataReadyState.ARMED;
        if (dataSavedAcknowledged)
        {
            _logger.LogInformation("Part {PartNumber} saved and acknowledged.", savedPart.PartNumber);
        }
        else
        {
            _logger.LogWarning("Part {PartNumber} is durable in local History, but its separate DATA SAVED acknowledgement failed.", savedPart.PartNumber);
        }
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
            _logger.LogError("D1075=1, Part {PartNumber} save failed: {Message}. Will retry while trigger remains HIGH.", record.PartNumber, message);
        }
        else
        {
            _logger.LogError(exception, "D1075=1, Part {PartNumber} save failed: {Message}. Will retry while trigger remains HIGH.", record.PartNumber, message);
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

    private void MarkPartSaved(string partNumber)
    {
        var normalized = NormalizePartNumber(partNumber);
        if (StringComparer.Ordinal.Equals(_observedPartNumber, normalized))
        {
            _observedPartAlreadySaved = true;
        }

        if (StringComparer.Ordinal.Equals(_pendingPartNumber, normalized))
        {
            _pendingPartNumber = null;
        }
    }

    private void LogLiveInputs(string partNumberRaw, string partNumber, string readyRaw, bool? readyHigh)
    {
        var partInput = $"{partNumberRaw}|{partNumber}";
        if (!StringComparer.Ordinal.Equals(_lastLoggedPartNumberInput, partInput))
        {
            _lastLoggedPartNumberInput = partInput;
            _logger.LogInformation("Part Number raw value received: {RawValue}", partNumberRaw);
            _logger.LogInformation("Decoded Part Number: {PartNumber}", partNumber.Length == 0 ? "<empty>" : partNumber);
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
        var pendingPartNumber = _pendingPartNumber ?? _pending?.Record.PartNumber ?? "<none>";
        var decodedReady = readyHigh.HasValue ? (readyHigh.Value ? "1" : "0") : "<unavailable>";
        var signature = $"{pendingPartNumber}|{decodedReady}|{alreadySaved}|{databaseReady}|{saveConditionSatisfied}|{reason}";
        if (StringComparer.Ordinal.Equals(_lastLoggedSaveDecision, signature))
        {
            return;
        }

        _lastLoggedSaveDecision = signature;
        _logger.LogInformation("Save condition satisfied: {SaveConditionSatisfied}", saveConditionSatisfied);
        if (!saveConditionSatisfied)
        {
            _logger.LogInformation(
                "SAVE SKIPPED: pendingPartNumber = {PendingPartNumber}; D1075 = {D1075}; alreadySaved = {AlreadySaved}; databaseReady = {DatabaseReady}. Reason: {Reason}",
                pendingPartNumber,
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

    private static string ReadPartNumber(IReadOnlyDictionary<string, PlcSignalValue> signals)
    {
        if (!signals.TryGetValue("Part Number", out var signal) || !string.IsNullOrWhiteSpace(signal.Error))
        {
            return "";
        }

        return NormalizePartNumber(Convert.ToString(signal.InterpretedValue ?? signal.RawValue, CultureInfo.InvariantCulture));
    }

    private static string NormalizePartNumber(string? value) => (value ?? "").Trim();

    private static bool IsValidPartNumber(string value) =>
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
