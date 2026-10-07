using DynaK.Service.Plc;

namespace DynaK.Service.Services;

public sealed class PlcHandshakeService : IDisposable
{
    private static readonly TimeSpan DataSavedPulseDuration = TimeSpan.FromSeconds(2);

    private readonly IPlcClient _plcClient;
    private readonly SettingsStore _settings;
    private readonly ILogger<PlcHandshakeService> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _pulseGate = new(1, 1);
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, bool> _lastWritten = new(StringComparer.OrdinalIgnoreCase);

    // DATA SAVED HIGH was confirmed, but the matching LOW has not yet
    // been confirmed.
    private bool _dataSavedHighNeedsClear;

    // A reconnect/session initialization successfully returned DATA SAVED
    // LOW after a previously interrupted pulse.
    private bool _dataSavedRecoveredByForcedLow;

    private bool _disposed;

    public PlcHandshakeService(
        IPlcClient plcClient,
        SettingsStore settings,
        ILogger<PlcHandshakeService> logger)
    {
        _plcClient = plcClient;
        _settings = settings;
        _logger = logger;
    }

    public void ResetSessionCache()
    {
        lock (_cacheGate)
        {
            _lastWritten.Clear();

            // This flag is only meaningful inside the current connected
            // recovery sequence. Do not carry it into a new session.
            _dataSavedRecoveredByForcedLow = false;

            // Deliberately DO NOT clear _dataSavedHighNeedsClear here.
            // If the connection dropped after HIGH succeeded, reconnect
            // must still force DATA SAVED LOW.
        }
    }

    public async Task InitializeConnectedSessionAsync(
        CancellationToken cancellationToken)
    {
        // Always establish a known LOW state first.
        await SetDataSavedLowAsync(cancellationToken);

        await SetCommunicationOkAsync(false, cancellationToken);
        await SetSystemReadyAsync(true, cancellationToken);
    }

    public Task SetSystemReadyAsync(
        bool ready,
        CancellationToken cancellationToken) =>
        WriteIfChangedAsync(
            PlcSafety.SystemReadySignalName,
            ready,
            cancellationToken);

    public Task SetCommunicationOkAsync(
        bool healthy,
        CancellationToken cancellationToken) =>
        WriteIfChangedAsync(
            PlcSafety.CommunicationOkSignalName,
            healthy,
            cancellationToken);

    public async Task<bool> PulseDataSavedAsync(
        CancellationToken cancellationToken)
    {
        if (!IsEnabled(PlcSafety.DataSavedSignalName))
        {
            _dataSavedHighNeedsClear = false;
            _dataSavedRecoveredByForcedLow = false;
            return true;
        }

        await _pulseGate.WaitAsync(cancellationToken);

        try
        {
            /*
             * A reconnect/session initialization may already have returned
             * DATA SAVED LOW after an interrupted HIGH pulse.
             *
             * In that case the outstanding acknowledgement is complete.
             * Do not create another HIGH pulse for the same saved record.
             */
            if (_dataSavedRecoveredByForcedLow)
            {
                _dataSavedRecoveredByForcedLow = false;

                _logger.LogInformation(
                    "DATA SAVED acknowledgement recovered after reconnect/session initialization returned the signal LOW.");

                return true;
            }

            /*
             * Previous HIGH succeeded but LOW failed.
             * Retry only LOW. Do not generate another HIGH.
             */
            if (_dataSavedHighNeedsClear)
            {
                var recovered = await WriteIfChangedAsync(
                    PlcSafety.DataSavedSignalName,
                    false,
                    cancellationToken);

                if (!recovered)
                {
                    _logger.LogWarning(
                        "DATA SAVED remains HIGH because the recovery LOW write failed.");

                    return false;
                }

                _dataSavedHighNeedsClear = false;

                _logger.LogInformation(
                    "DATA SAVED acknowledgement recovered by successfully returning the signal LOW.");

                return true;
            }

            /*
             * Start the real acknowledgement pulse.
             */
            var highWritten = await WriteIfChangedAsync(
                PlcSafety.DataSavedSignalName,
                true,
                cancellationToken);

            if (!highWritten)
            {
                return false;
            }

            _dataSavedHighNeedsClear = true;

            /*
             * The actual configured PLC output remains HIGH for
             * approximately two seconds.
             */
            await Task.Delay(
                DataSavedPulseDuration,
                cancellationToken);

            var lowWritten = await WriteIfChangedAsync(
                PlcSafety.DataSavedSignalName,
                false,
                cancellationToken);

            if (!lowWritten)
            {
                // Keep _dataSavedHighNeedsClear = true.
                // A later retry/reconnect will attempt only the missing LOW.
                _logger.LogWarning(
                    "DATA SAVED HIGH succeeded, but the LOW reset failed. The acknowledgement remains pending.");

                return false;
            }

            _dataSavedHighNeedsClear = false;

            return true;
        }
        finally
        {
            _pulseGate.Release();
        }
    }

    public async Task SetDataSavedLowAsync(
        CancellationToken cancellationToken)
    {
        await _pulseGate.WaitAsync(cancellationToken);

        try
        {
            var recoveringInterruptedPulse = _dataSavedHighNeedsClear;

            var cleared = await WriteIfChangedAsync(
                PlcSafety.DataSavedSignalName,
                false,
                cancellationToken);

            if (!cleared)
            {
                if (recoveringInterruptedPulse)
                {
                    _logger.LogWarning(
                        "DATA SAVED could not be returned LOW while recovering an interrupted acknowledgement.");
                }

                return;
            }

            if (recoveringInterruptedPulse)
            {
                /*
                 * The LOW performed during reconnect/session initialization
                 * completed the outstanding pulse.
                 */
                _dataSavedRecoveredByForcedLow = true;

                _logger.LogInformation(
                    "DATA SAVED was returned LOW while recovering an interrupted acknowledgement.");
            }

            _dataSavedHighNeedsClear = false;
        }
        finally
        {
            _pulseGate.Release();
        }
    }

    private async Task<bool> WriteIfChangedAsync(
        string signalName,
        bool value,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled(signalName))
        {
            lock (_cacheGate)
            {
                _lastWritten.Remove(signalName);
            }

            return true;
        }

        await _writeGate.WaitAsync(cancellationToken);

        try
        {
            lock (_cacheGate)
            {
                if (_lastWritten.TryGetValue(signalName, out var previous) &&
                    previous == value)
                {
                    return true;
                }
            }

            /*
             * Real PLC write.
             *
             * MitsubishiModbusPlcClient resolves signalName against the
             * configured PLC mapping. Therefore Data Saved remains editable
             * and is NOT hard-coded to D2110 here.
             */
            await _plcClient.WriteConfiguredSignalAsync(
                signalName,
                value,
                cancellationToken);

            lock (_cacheGate)
            {
                _lastWritten[signalName] = value;
            }

            _logger.LogInformation(
                "{SignalLabel} -> {State}",
                SignalLabel(signalName),
                value ? "ON" : "OFF");

            return true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_cacheGate)
            {
                _lastWritten.Remove(signalName);
            }

            _logger.LogWarning(
                ex,
                "{SignalName} handshake write failed",
                signalName);

            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private bool IsEnabled(string signalName) =>
        _settings.Current.SignalMappings.Any(mapping =>
            mapping.Enabled &&
            string.Equals(
                mapping.SignalName,
                signalName,
                StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // AcquisitionWorker owns physical PLC shutdown cleanup.
        ResetSessionCache();

        _writeGate.Dispose();
        _pulseGate.Dispose();
    }

    private static string SignalLabel(string signalName) => signalName switch
    {
        PlcSafety.SystemReadySignalName => "SYSTEM READY",
        PlcSafety.CommunicationOkSignalName => "COMMUNICATION OK",
        PlcSafety.DataSavedSignalName => "DATA SAVED",
        _ => signalName
    };
}
