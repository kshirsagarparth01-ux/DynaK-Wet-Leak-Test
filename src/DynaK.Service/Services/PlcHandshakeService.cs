using DynaK.Service.Plc;

namespace DynaK.Service.Services;

public sealed class PlcHandshakeService : IDisposable
{
    private static readonly TimeSpan DataSavedPulseDuration = TimeSpan.FromSeconds(1);

    private readonly IPlcClient _plcClient;
    private readonly SettingsStore _settings;
    private readonly ILogger<PlcHandshakeService> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _pulseGate = new(1, 1);
    private readonly object _timerGate = new();
    private readonly Dictionary<string, bool> _lastWritten = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _dataSavedReset;
    private Task? _dataSavedResetTask;
    private bool _disposed;

    public PlcHandshakeService(IPlcClient plcClient, SettingsStore settings, ILogger<PlcHandshakeService> logger)
    {
        _plcClient = plcClient;
        _settings = settings;
        _logger = logger;
    }

    public void ResetSessionCache()
    {
        lock (_timerGate)
        {
            _dataSavedReset?.Cancel();
            _dataSavedReset?.Dispose();
            _dataSavedReset = null;
            _dataSavedResetTask = null;
            _lastWritten.Clear();
        }
    }

    public async Task InitializeConnectedSessionAsync(CancellationToken cancellationToken)
    {
        await SetDataSavedLowAsync(cancellationToken);
        await SetCommunicationOkAsync(false, cancellationToken);
        await SetSystemReadyAsync(true, cancellationToken);
    }

    public Task SetSystemReadyAsync(bool ready, CancellationToken cancellationToken) =>
        WriteIfChangedAsync(PlcSafety.SystemReadySignalName, ready, cancellationToken);

    public Task SetCommunicationOkAsync(bool healthy, CancellationToken cancellationToken) =>
        WriteIfChangedAsync(PlcSafety.CommunicationOkSignalName, healthy, cancellationToken);

    public async Task<bool> PulseDataSavedAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled(PlcSafety.DataSavedSignalName))
        {
            return true;
        }

        await _pulseGate.WaitAsync(cancellationToken);
        try
        {
            lock (_timerGate)
            {
                if (_dataSavedResetTask is { IsCompleted: false })
                {
                    _logger.LogWarning("DATA SAVED pulse is already active; the next acknowledgement will retry after it returns OFF.");
                    return false;
                }
            }

            if (!await WriteIfChangedAsync(PlcSafety.DataSavedSignalName, true, cancellationToken))
            {
                return false;
            }

            lock (_timerGate)
            {
                _dataSavedReset?.Dispose();
                _dataSavedReset = new CancellationTokenSource();
                _dataSavedResetTask = ResetDataSavedLaterAsync(_dataSavedReset.Token);
            }

            return true;
        }
        finally
        {
            _pulseGate.Release();
        }
    }

    public async Task SetDataSavedLowAsync(CancellationToken cancellationToken)
    {
        lock (_timerGate)
        {
            _dataSavedReset?.Cancel();
            _dataSavedReset?.Dispose();
            _dataSavedReset = null;
            _dataSavedResetTask = null;
        }

        await WriteIfChangedAsync(PlcSafety.DataSavedSignalName, false, cancellationToken);
    }

    private async Task ResetDataSavedLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DataSavedPulseDuration, cancellationToken);
            await WriteIfChangedAsync(PlcSafety.DataSavedSignalName, false, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> WriteIfChangedAsync(string signalName, bool value, CancellationToken cancellationToken)
    {
        if (!IsEnabled(signalName))
        {
            _lastWritten.Remove(signalName);
            return true;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (_lastWritten.TryGetValue(signalName, out var previous) && previous == value)
            {
                return true;
            }

            await _plcClient.WriteConfiguredSignalAsync(signalName, value, cancellationToken);
            _lastWritten[signalName] = value;
            _logger.LogInformation("{SignalLabel} -> {State}", SignalLabel(signalName), value ? "ON" : "OFF");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastWritten.Remove(signalName);
            _logger.LogWarning(ex, "{SignalName} handshake write failed", signalName);
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private bool IsEnabled(string signalName) =>
        _settings.Current.SignalMappings.Any(mapping =>
            mapping.Enabled && string.Equals(mapping.SignalName, signalName, StringComparison.OrdinalIgnoreCase));

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
