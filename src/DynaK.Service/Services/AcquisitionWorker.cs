using DynaK.Service.Configuration;
using DynaK.Service.Data;
using DynaK.Service.Models;
using DynaK.Service.Plc;

namespace DynaK.Service.Services;

public sealed class AcquisitionWorker : BackgroundService
{
    private const int CommunicationFailureThreshold = 3;

    private readonly IPlcClient _plcClient;
    private readonly EventRepository _events;
    private readonly SettingsStore _settingsStore;
    private readonly AcquisitionState _state;
    private readonly StationRuntimeControl _runtime;
    private readonly PlcHandshakeService _handshake;
    private readonly PartDataReadyService _partDataReady;
    private readonly ILogger<AcquisitionWorker> _logger;

    private string? _activeConfiguration;
    private bool _connected;
    private bool _hasConnectedBefore;
    private bool _connectionAttemptLogged;
    private int _pollFailureCount;
    private readonly Dictionary<string, string?> _lastLoggedSignalValues = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastCommunicationErrorSignature;
    private DateTimeOffset _lastCommunicationErrorLoggedAt;

    public AcquisitionWorker(
        IPlcClient plcClient,
        EventRepository events,
        SettingsStore settingsStore,
        AcquisitionState state,
        StationRuntimeControl runtime,
        PlcHandshakeService handshake,
        PartDataReadyService partDataReady,
        ILogger<AcquisitionWorker> logger)
    {
        _plcClient = plcClient;
        _events = events;
        _settingsStore = settingsStore;
        _state = state;
        _runtime = runtime;
        _handshake = handshake;
        _partDataReady = partDataReady;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialSettings = _settingsStore.Current;
        await _events.AddAsync(initialSettings.StationId, "BACKGROUND_SERVICE_STARTED", "INFO", null, "Acquisition background service started", DateTimeOffset.Now, stoppingToken);
        _logger.LogInformation("Acquisition background service started for station {StationId}", initialSettings.StationId);
        _state.ConfigurePlc(initialSettings);
        _state.MarkStopped(initialSettings.Plc.IsConfigured);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var session = await _runtime.WaitForStartAsync(stoppingToken);
                ResetSessionTracking();
                _state.MarkStarting();
                var settings = _settingsStore.Current;
                await _events.AddAsync(settings.StationId, "STATION_START_REQUESTED", "INFO", null, "Operator started the station runtime", DateTimeOffset.Now, stoppingToken);

                using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, session.StoppingToken);
                try
                {
                    await RunStationSessionAsync(sessionCancellation.Token);
                }
                catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
                {
                }
                finally
                {
                    settings = _settingsStore.Current;
                    await _partDataReady.EndSessionAsync(CancellationToken.None);
                    await StopConnectionAsync(settings);
                    _state.MarkStopped(settings.Plc.IsConfigured);
                    _runtime.CompleteStop(session);
                    await _events.AddAsync(settings.StationId, "STATION_STOPPED", "INFO", null, "Station runtime stopped", DateTimeOffset.Now, CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await ShutdownAsync();
        }
    }

    private async Task RunStationSessionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = _settingsStore.Current;
            var configuration = PlcClientConfiguration.From(settings);

            if (!configuration.IsConfigured)
            {
                await ResetConnectionAsync(cancellationToken);
                _activeConfiguration = null;
                await MarkConfigurationErrorAsync(settings, "PLC_NOT_CONFIGURED", "PLC communication settings are not configured.", cancellationToken);
                await DelayAsync(settings.Plc.ReconnectIntervalMs, cancellationToken);
                continue;
            }

            var mappingErrors = PlcSignalMapping.GetOperationalValidationErrors(configuration.SignalMappings);
            if (mappingErrors.Count > 0)
            {
                await ResetConnectionAsync(cancellationToken);
                _activeConfiguration = null;
                await MarkConfigurationErrorAsync(settings, "PLC_MAPPING_INCOMPLETE", $"PLC mapping incomplete: {string.Join(" ", mappingErrors)}", cancellationToken);
                await DelayAsync(settings.Plc.ReconnectIntervalMs, cancellationToken);
                continue;
            }

            await _events.ClearActiveByTypeAsync(settings.StationId, "CONFIGURATION_ERROR", DateTimeOffset.Now, cancellationToken);

            var fingerprint = configuration.Fingerprint();
            if (!StringComparer.Ordinal.Equals(_activeConfiguration, fingerprint))
            {
                if (_activeConfiguration is not null)
                {
                    await _events.AddAsync(settings.StationId, "CONFIGURATION_CHANGED", "INFO", null, "PLC configuration changed; reconnecting.", DateTimeOffset.Now, cancellationToken);
                }

                await ResetConnectionAsync(cancellationToken);
                _activeConfiguration = fingerprint;
                _connectionAttemptLogged = false;
                _state.ConfigurePlc(settings);
                LogNetworkDiagnostic(settings);
            }

            if (!_connected)
            {
                await ConnectAsync(settings, configuration, cancellationToken);
                continue;
            }

            await PollConnectedClientAsync(settings, cancellationToken);
        }
    }

    private async Task ConnectAsync(AppSettings settings, PlcClientConfiguration configuration, CancellationToken cancellationToken)
    {
        var status = _hasConnectedBefore ? PlcServiceStatus.Reconnecting : PlcServiceStatus.Connecting;
        if (_hasConnectedBefore)
        {
            _state.MarkConnectionError(status, "Attempting to reconnect to the PLC.");
        }
        else
        {
            _state.MarkStarting();
        }
        if (!_connectionAttemptLogged)
        {
            await _events.AddAsync(
                settings.StationId,
                _hasConnectedBefore ? "PLC_RECONNECT_STARTED" : "PLC_CONNECTION_STARTED",
                "INFO",
                null,
                _hasConnectedBefore ? "PLC reconnect attempt started" : "PLC connection attempt started",
                DateTimeOffset.Now,
                cancellationToken);
            _connectionAttemptLogged = true;
        }

        try
        {
            var reconnecting = _hasConnectedBefore;
            await _plcClient.ConnectAsync(configuration, cancellationToken);
            _connected = await _plcClient.IsConnectedAsync(cancellationToken);

            if (!_connected)
            {
                await MarkReconnectFailedAsync(settings, "PLC_DISCONNECTED", "PLC client did not report a connected state.", cancellationToken);
                await MarkDisconnectedAsync(settings, "PLC_DISCONNECTED", "PLC client did not report a connected state.", cancellationToken);
                await DelayAsync(settings.Plc.ReconnectIntervalMs, cancellationToken);
                return;
            }

            _hasConnectedBefore = true;
            _connectionAttemptLogged = false;
            _pollFailureCount = 0;
            _state.MarkStarting();
            await _handshake.InitializeConnectedSessionAsync(cancellationToken);
            await _events.ClearActiveByTypeAsync(settings.StationId, "PLC_DISCONNECTED", DateTimeOffset.Now, cancellationToken);
            await _events.ClearActiveByTypeAsync(settings.StationId, "SERVICE_ERROR", DateTimeOffset.Now, cancellationToken);
            await _events.ClearActiveByTypeAsync(settings.StationId, "PLC_RECONNECT_FAILED", DateTimeOffset.Now, cancellationToken);
            await _events.AddAsync(settings.StationId, "PLC_CONNECTED", "INFO", null, "PLC connected", DateTimeOffset.Now, cancellationToken);
            _logger.LogInformation(reconnecting ? "PLC reconnected" : "PLC connected");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            LogCommunicationFailure("PLC connection attempt timed out", ex);
            _state.MarkPlcError(settings, "TCP_CONNECTION_TIMEOUT", "PLC TCP connection attempt timed out.");
            await MarkReconnectFailedAsync(settings, "PLC_CONNECTION_TIMEOUT", "PLC connection attempt timed out.", cancellationToken);
            await MarkDisconnectedAsync(settings, "PLC_CONNECTION_TIMEOUT", "PLC connection attempt timed out.", cancellationToken);
            await DelayAsync(settings.Plc.ReconnectIntervalMs, cancellationToken);
        }
        catch (PlcClientUnavailableException ex)
        {
            LogCommunicationFailure("PLC client unavailable", ex);
            _connected = false;
            _state.MarkPlcError(settings, "COMMUNICATION_LIBRARY_ERROR", ex.Message);
            _state.MarkConnectionError(PlcServiceStatus.ServiceError, ex.Message);
            await _events.StartActiveAsync(settings.StationId, "SERVICE_ERROR", "ERROR", "PLC_CLIENT_PENDING", ex.Message, DateTimeOffset.Now, cancellationToken);
            await DelayAsync(settings.Plc.ReconnectIntervalMs, cancellationToken);
        }
        catch (Exception ex)
        {
            LogCommunicationFailure("PLC connection attempt failed", ex);
            var category = PlcErrorClassifier.Classify(ex);
            _state.MarkPlcError(settings, category, ex.Message);
            await MarkReconnectFailedAsync(settings, category, ex.Message, cancellationToken);
            await MarkDisconnectedAsync(settings, category, ex.Message, cancellationToken);
            await DelayAsync(settings.Plc.ReconnectIntervalMs, cancellationToken);
        }
    }

    private async Task PollConnectedClientAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var poll = await _plcClient.ReadPollAsync(cancellationToken);
            var machine = poll.MachineStatus;
            if (!machine.IsConnected)
            {
                await TrackCommunicationFailureAsync(settings, machine.ErrorCode ?? "PLC_DISCONNECTED", machine.ErrorDescription ?? "PLC disconnected", cancellationToken);
                return;
            }

            _pollFailureCount = 0;
            var livePlcSignals = _state.UpdatePlcRead(settings, poll.Signals, machine.Timestamp);
            await _partDataReady.ProcessPollAsync(settings, livePlcSignals, cancellationToken);
            _state.MarkRunning(machine.IsMachineRunning, machine.ErrorDescription);
            await _handshake.SetCommunicationOkAsync(true, cancellationToken);
            await _events.ClearActiveByTypeAsync(settings.StationId, "PLC_DISCONNECTED", DateTimeOffset.Now, cancellationToken);
            LogCommissioningSignals(settings, poll.Signals);

            await DelayAsync(settings.Plc.PollingIntervalMs, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlcSignalMappingException ex)
        {
            _logger.LogError(ex, "PLC mapping conversion failed");
            await _handshake.SetCommunicationOkAsync(false, cancellationToken);
            _state.MarkPlcError(settings, "INVALID_ADDRESS_OR_MAPPING", ex.Message);
            _state.MarkConnectionError(PlcServiceStatus.ConfigurationError, ex.Message);
            await _events.StartActiveAsync(settings.StationId, "CONFIGURATION_ERROR", "ERROR", "PLC_MAPPING_CONVERSION", ex.Message, DateTimeOffset.Now, cancellationToken);
            await DelayAsync(settings.Plc.PollingIntervalMs, cancellationToken);
        }
        catch (Exception ex)
        {
            LogCommunicationFailure("Acquisition polling failed", ex);
            var category = PlcErrorClassifier.Classify(ex);
            _state.MarkPlcError(settings, category, ex.Message);
            await _events.StartActiveAsync(settings.StationId, "ACQUISITION_ERROR", "ERROR", ex.GetType().Name, ex.Message, DateTimeOffset.Now, cancellationToken);
            await TrackCommunicationFailureAsync(settings, category, ex.Message, cancellationToken);
        }
    }

    private async Task MarkConfigurationErrorAsync(AppSettings settings, string code, string description, CancellationToken cancellationToken)
    {
        _state.MarkConnectionError(code == "PLC_NOT_CONFIGURED" ? PlcServiceStatus.NotConfigured : PlcServiceStatus.ConfigurationError, description);
        await _events.StartActiveAsync(settings.StationId, "CONFIGURATION_ERROR", "WARN", code, description, DateTimeOffset.Now, cancellationToken);
    }

    private async Task MarkDisconnectedAsync(AppSettings settings, string code, string description, CancellationToken cancellationToken)
    {
        _state.MarkConnectionError(_hasConnectedBefore ? PlcServiceStatus.Reconnecting : PlcServiceStatus.Disconnected, description);
        await _events.StartActiveAsync(settings.StationId, "PLC_DISCONNECTED", "ERROR", code, description, DateTimeOffset.Now, cancellationToken);
        _logger.LogWarning("PLC disconnected: {Code} - {Description}", code, description);
    }

    private async Task MarkReconnectFailedAsync(AppSettings settings, string code, string description, CancellationToken cancellationToken)
    {
        if (!_hasConnectedBefore)
        {
            return;
        }

        await _events.StartActiveAsync(settings.StationId, "PLC_RECONNECT_FAILED", "ERROR", code, description, DateTimeOffset.Now, cancellationToken);
    }

    private async Task ResetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connected)
        {
            await _handshake.SetCommunicationOkAsync(false, cancellationToken);
            await _handshake.SetSystemReadyAsync(false, cancellationToken);
            await _handshake.SetDataSavedLowAsync(cancellationToken);
        }

        try
        {
            await _plcClient.DisconnectAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PLC disconnect failed during reset");
        }

        _connected = false;
        _connectionAttemptLogged = false;
        _handshake.ResetSessionCache();
    }

    private async Task StopConnectionAsync(AppSettings settings)
    {
        if (_connected)
        {
            await _handshake.SetCommunicationOkAsync(false, CancellationToken.None);
            await _handshake.SetSystemReadyAsync(false, CancellationToken.None);
            await _handshake.SetDataSavedLowAsync(CancellationToken.None);
        }

        try
        {
            await _plcClient.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PLC disconnect failed while stopping station runtime");
        }

        _connected = false;
        _connectionAttemptLogged = false;
        _handshake.ResetSessionCache();
    }

    private void ResetSessionTracking()
    {
        _activeConfiguration = null;
        _connected = false;
        _hasConnectedBefore = false;
        _connectionAttemptLogged = false;
        _pollFailureCount = 0;
        _lastLoggedSignalValues.Clear();
        _lastCommunicationErrorSignature = null;
        _lastCommunicationErrorLoggedAt = default;
        _handshake.ResetSessionCache();
        _partDataReady.ResetSession();
    }

    private async Task ShutdownAsync()
    {
        var settings = _settingsStore.Current;

        if (_connected)
        {
            await _handshake.SetCommunicationOkAsync(false, CancellationToken.None);
            await _handshake.SetSystemReadyAsync(false, CancellationToken.None);
            await _handshake.SetDataSavedLowAsync(CancellationToken.None);
        }

        try
        {
            await _plcClient.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PLC disconnect failed during shutdown");
        }

        _state.MarkStopped(settings.Plc.IsConfigured);
        _handshake.ResetSessionCache();
        await _events.AddAsync(settings.StationId, "BACKGROUND_SERVICE_STOPPED", "INFO", null, "Acquisition background service stopped", DateTimeOffset.Now, CancellationToken.None);
        _logger.LogInformation("Acquisition background service stopped for station {StationId}", settings.StationId);
    }

    private async Task TrackCommunicationFailureAsync(AppSettings settings, string code, string description, CancellationToken cancellationToken)
    {
        _pollFailureCount++;
        _state.MarkConnectionError(
            PlcServiceStatus.Error,
            $"PLC communication warning { _pollFailureCount }/{ CommunicationFailureThreshold }: { description }");

        if (_pollFailureCount < CommunicationFailureThreshold)
        {
            await DelayAsync(settings.Plc.PollingIntervalMs, cancellationToken);
            return;
        }

        await _handshake.SetCommunicationOkAsync(false, cancellationToken);
        _connected = false;
        _pollFailureCount = 0;
        _state.MarkPlcError(settings, code, description);
        await MarkDisconnectedAsync(settings, code, description, cancellationToken);
        _handshake.ResetSessionCache();
        await DelayAsync(settings.Plc.ReconnectIntervalMs, cancellationToken);
    }

    private void LogNetworkDiagnostic(AppSettings settings)
    {
        var interfaces = PlcNetworkDiagnostics.Capture(settings.Plc.IpAddress);
        if (interfaces.Count == 0)
        {
            _logger.LogWarning("No active PC IPv4 network interface was detected for PLC {PlcIpAddress}", settings.Plc.IpAddress);
            return;
        }

        foreach (var item in interfaces)
        {
            _logger.LogInformation(
                "PC network {InterfaceName} ({InterfaceType}): {Ipv4Address} mask {SubnetMask}; PLC {PlcIpAddress}; compatible subnet: {Compatible}",
                item.Name,
                item.Type,
                item.Ipv4Address,
                item.SubnetMask ?? "unknown",
                settings.Plc.IpAddress,
                item.CompatibleWithPlc ? "YES" : "NO");
        }
    }

    private void LogCommissioningSignals(AppSettings settings, IReadOnlyDictionary<string, PlcSignalValue> signals)
    {
        foreach (var signalName in new[]
        {
            "Target Parts Per Shift",
            "Actual Part Count",
            PlcSignalMapping.OkResultSignalName,
            PlcSignalMapping.NgResultSignalName,
            PlcSignalMapping.PartDataReadySignalName,
            "Part Number"
        })
        {
            if (!signals.TryGetValue(signalName, out var value))
            {
                continue;
            }

            var raw = Convert.ToString(value.RawValue, System.Globalization.CultureInfo.InvariantCulture);
            if (_lastLoggedSignalValues.TryGetValue(signalName, out var previous) && StringComparer.Ordinal.Equals(previous, raw))
            {
                continue;
            }

            _lastLoggedSignalValues[signalName] = raw;
            var address = settings.SignalMappings.FirstOrDefault(mapping =>
                string.Equals(mapping.SignalName, signalName, StringComparison.OrdinalIgnoreCase))?.Address ?? "unknown";
            _logger.LogInformation("PLC read {Address} ({SignalName}) = {RawValue}", address, signalName, raw ?? "null");
        }
    }

    private void LogCommunicationFailure(string context, Exception exception)
    {
        var signature = $"{context}|{exception.GetBaseException().GetType().FullName}|{exception.GetBaseException().Message}";
        var now = DateTimeOffset.Now;
        if (StringComparer.Ordinal.Equals(signature, _lastCommunicationErrorSignature) &&
            now - _lastCommunicationErrorLoggedAt < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastCommunicationErrorSignature = signature;
        _lastCommunicationErrorLoggedAt = now;
        _logger.LogError(exception, "{Context}", context);
    }

    private static async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 100, 300000)), cancellationToken);
    }
}
