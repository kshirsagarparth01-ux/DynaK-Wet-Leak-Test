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
        }
    }

    public async Task InitializeConnectedSessionAsync(CancellationToken cancellationToken)
    {
        var dataSavedLow = await SetDataSavedLowAsync(cancellationToken);
        if (!dataSavedLow)
        {
            throw new InvalidOperationException(
                "Connected PLC session could not establish DATA SAVED in the OFF state.");
        }

        await SetCommunicationOkAsync(false, cancellationToken);
        await SetSystemReadyAsync(true, cancellationToken);
    }

    public Task SetSystemReadyAsync(bool ready, CancellationToken cancellationToken) =>
        WriteIfChangedAsync(
            PlcSafety.SystemReadySignalName,
            ready,
            cancellationToken);

    public Task SetCommunicationOkAsync(bool healthy, CancellationToken cancellationToken) =>
        WriteIfChangedAsync(
            PlcSafety.CommunicationOkSignalName,
            healthy,
            cancellationToken);

    public async Task<bool> PulseDataSavedAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled(PlcSafety.DataSavedSignalName))
        {
            return true;
        }

        await _pulseGate.WaitAsync(cancellationToken);

        try
        {
            var highWritten = await WriteIfChangedAsync(
                PlcSafety.DataSavedSignalName,
                true,
                cancellationToken);

            if (!highWritten)
            {
                return false;
            }

            try
            {
                await Task.Delay(DataSavedPulseDuration, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var clearedAfterCancellation = await WriteIfChangedAsync(
                    PlcSafety.DataSavedSignalName,
                    false,
                    CancellationToken.None);

                if (!clearedAfterCancellation)
                {
                    _logger.LogError(
                        "DATA SAVED could not be forced OFF after its pulse was cancelled.");
                }

                throw;
            }

            var lowWritten = await WriteIfChangedAsync(
                PlcSafety.DataSavedSignalName,
                false,
                CancellationToken.None);

            if (!lowWritten)
            {
                _logger.LogWarning(
                    "DATA SAVED ON write succeeded, but the required OFF write failed. " +
                    "The acknowledgement must be retried.");

                return false;
            }

            return true;
        }
        finally
        {
            _pulseGate.Release();
        }
    }

    public async Task<bool> SetDataSavedLowAsync(CancellationToken cancellationToken)
    {
        var cleared = await WriteIfChangedAsync(
            PlcSafety.DataSavedSignalName,
            false,
            cancellationToken);

        if (!cleared)
        {
            _logger.LogWarning(
                "Failed to establish DATA SAVED in the OFF state.");
        }

        return cleared;
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
