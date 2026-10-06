using DynaK.Service.Models;
using DynaK.Service.Plc;
using DynaK.Service.Configuration;
using System.Collections.ObjectModel;
using System.Globalization;

namespace DynaK.Service.Services;

public sealed class AcquisitionState
{
    private readonly object _gate = new();
    private ProductionRecord? _currentRecord;
    private PlcServiceStatus _serviceStatus = PlcServiceStatus.Disconnected;
    private StationRuntimeStatus _runtimeStatus = StationRuntimeStatus.Stopped;
    private bool _machineRunning;
    private string? _lastError;
    private IReadOnlyDictionary<string, PlcSignalValue> _livePlcSignals =
        new Dictionary<string, PlcSignalValue>(StringComparer.OrdinalIgnoreCase);
    private PlcCommissioningDiagnostic? _plcDiagnostic;
    private DateTimeOffset _statusChangedAt = DateTimeOffset.Now;

    public void ConfigurePlc(Configuration.AppSettings settings)
    {
        lock (_gate)
        {
            _plcDiagnostic = PlcCommissioningDiagnostic.From(settings);
        }
    }

    public IReadOnlyDictionary<string, PlcSignalValue> UpdatePlcRead(
        Configuration.AppSettings settings,
        IReadOnlyDictionary<string, PlcSignalValue> signals,
        DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            _livePlcSignals = new ReadOnlyDictionary<string, PlcSignalValue>(
                new Dictionary<string, PlcSignalValue>(signals, StringComparer.OrdinalIgnoreCase));
            var diagnostic = _plcDiagnostic ?? PlcCommissioningDiagnostic.From(settings);
            _plcDiagnostic = diagnostic with
            {
                LastSuccessfulRead = timestamp,
                LastErrorCategory = null,
                LastError = null,
                Signals = PlcCommissioningDiagnostic.BuildSignals(settings.SignalMappings, _livePlcSignals)
            };
            return _livePlcSignals;
        }
    }

    public void MarkPlcError(Configuration.AppSettings settings, string category, string error)
    {
        lock (_gate)
        {
            var diagnostic = _plcDiagnostic ?? PlcCommissioningDiagnostic.From(settings);
            _plcDiagnostic = diagnostic with { LastErrorCategory = category, LastError = error };
        }
    }

    public void Update(ProductionRecord record)
    {
        lock (_gate)
        {
            _currentRecord = record;
            SetStatus(PlcServiceStatus.Connected);
            SetRuntimeStatus(StationRuntimeStatus.Running);
            _machineRunning = record.IsMachineRunning;
            _lastError = string.Equals(record.ErrorDescription, "No Error", StringComparison.OrdinalIgnoreCase) ? null : record.ErrorDescription;
        }
    }

    public void ClearProductionData()
    {
        lock (_gate)
        {
            _currentRecord = null;
            _machineRunning = false;
            ClearLiveValues();
        }
    }

    public void MarkStarting()
    {
        lock (_gate)
        {
            SetRuntimeStatus(StationRuntimeStatus.Starting);
            SetStatus(PlcServiceStatus.Connecting);
            _machineRunning = false;
            _lastError = null;
            ClearLiveValues();
        }
    }

    public void MarkRunning(bool machineRunning, string? machineError)
    {
        lock (_gate)
        {
            SetRuntimeStatus(StationRuntimeStatus.Running);
            SetStatus(PlcServiceStatus.Connected);
            _machineRunning = machineRunning;
            _lastError = machineError;
        }
    }

    public void MarkConnectionError(PlcServiceStatus serviceStatus, string error)
    {
        lock (_gate)
        {
            SetRuntimeStatus(StationRuntimeStatus.ConnectionError);
            SetStatus(serviceStatus);
            _machineRunning = false;
            _lastError = error;
            ClearLiveValues();
        }
    }

    public void MarkStopping()
    {
        lock (_gate)
        {
            SetRuntimeStatus(StationRuntimeStatus.Stopping);
            _machineRunning = false;
        }
    }

    public void MarkStopped(bool plcConfigured)
    {
        lock (_gate)
        {
            SetRuntimeStatus(StationRuntimeStatus.Stopped);
            SetStatus(plcConfigured ? PlcServiceStatus.Disconnected : PlcServiceStatus.NotConfigured);
            _machineRunning = false;
            _lastError = null;
            ClearLiveValues();
        }
    }

    public void MarkStatus(PlcServiceStatus serviceStatus, bool machineRunning, string? lastError)
    {
        lock (_gate)
        {
            SetStatus(serviceStatus);
            _machineRunning = machineRunning;
            _lastError = lastError;
        }
    }

    public PlantState Snapshot(
        IReadOnlyList<LogicalPart> recent,
        Configuration.AppSettings settings,
        Configuration.ShiftWindow shiftWindow,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            var livePlcSignals = _livePlcSignals;
            var liveTargetParts = ReadInt(livePlcSignals, "Target Parts Per Shift");
            var liveActualCount = ReadInt(livePlcSignals, "Actual Part Count");
            var resolvedResult = PlcResultResolver.Resolve(livePlcSignals);
            var liveResultRaw = resolvedResult.RawText;
            var liveResult = resolvedResult.HasSignal ? resolvedResult.ResolvedText : null;
            var currentPart = _currentRecord is not null &&
                _currentRecord.Timestamp >= shiftWindow.StartsAt &&
                _currentRecord.Timestamp < shiftWindow.EndsAt
                    ? _currentRecord
                    : null;

            return new PlantState(
                settings.StationId,
                settings.StationName,
                now,
                shiftWindow.Shift.Name,
                _runtimeStatus,
                RuntimeStatusText(_runtimeStatus),
                _serviceStatus,
                StatusText(_serviceStatus),
                _statusChangedAt,
                settings.Plc.IsConfigured,
                _serviceStatus == PlcServiceStatus.Connected,
                _machineRunning,
                liveTargetParts,
                liveActualCount,
                currentPart?.OkCount ?? 0,
                currentPart?.NgCount ?? 0,
                currentPart?.ReworkCount ?? 0,
                liveResultRaw,
                liveResult,
                livePlcSignals,
                currentPart,
                recent,
                _lastError,
                _plcDiagnostic ?? PlcCommissioningDiagnostic.From(settings));
        }
    }

    private void ClearLiveValues()
    {
        _livePlcSignals = new Dictionary<string, PlcSignalValue>(StringComparer.OrdinalIgnoreCase);
    }

    private static int? ReadInt(IReadOnlyDictionary<string, PlcSignalValue> signals, string signalName)
    {
        if (!signals.TryGetValue(signalName, out var signal))
        {
            return null;
        }

        var text = Convert.ToString(signal.InterpretedValue, CultureInfo.InvariantCulture);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static (string? Raw, string? Interpreted) ReadText(
        IReadOnlyDictionary<string, PlcSignalValue> signals,
        string signalName)
    {
        if (!signals.TryGetValue(signalName, out var signal))
        {
            return (null, null);
        }

        return (
            Convert.ToString(signal.RawValue, CultureInfo.InvariantCulture),
            Convert.ToString(signal.InterpretedValue, CultureInfo.InvariantCulture));
    }

    private void SetStatus(PlcServiceStatus serviceStatus)
    {
        if (_serviceStatus == serviceStatus)
        {
            return;
        }

        _serviceStatus = serviceStatus;
        _statusChangedAt = DateTimeOffset.Now;
    }

    private void SetRuntimeStatus(StationRuntimeStatus runtimeStatus)
    {
        if (_runtimeStatus == runtimeStatus)
        {
            return;
        }

        _runtimeStatus = runtimeStatus;
        _statusChangedAt = DateTimeOffset.Now;
    }

    private static string RuntimeStatusText(StationRuntimeStatus runtimeStatus) => runtimeStatus switch
    {
        StationRuntimeStatus.Stopped => "STOPPED",
        StationRuntimeStatus.Starting => "STARTING / CONNECTING",
        StationRuntimeStatus.Running => "RUNNING",
        StationRuntimeStatus.ConnectionError => "CONNECTION ERROR",
        StationRuntimeStatus.Stopping => "STOPPING",
        _ => runtimeStatus.ToString().ToUpperInvariant()
    };

    private static string StatusText(PlcServiceStatus serviceStatus) => serviceStatus switch
    {
        PlcServiceStatus.NotConfigured => "NOT CONFIGURED",
        PlcServiceStatus.Connecting => "CONNECTING",
        PlcServiceStatus.Connected => "CONNECTED",
        PlcServiceStatus.Disconnected => "DISCONNECTED",
        PlcServiceStatus.Reconnecting => "RECONNECTING",
        PlcServiceStatus.Error => "CONNECTED BUT READ FAILED",
        PlcServiceStatus.ConfigurationError => "CONFIGURATION ERROR",
        PlcServiceStatus.ServiceError => "SERVICE ERROR",
        _ => serviceStatus.ToString().ToUpperInvariant()
    };
}

public sealed record PlantState(
    string StationId,
    string StationName,
    DateTimeOffset Now,
    string Shift,
    StationRuntimeStatus RuntimeStatus,
    string RuntimeStatusText,
    PlcServiceStatus ServiceStatus,
    string ServiceStatusText,
    DateTimeOffset ServiceStatusChangedAt,
    bool PlcConfigured,
    bool PlcConnected,
    bool MachineRunning,
    int? TargetPartsPerShift,
    int? ActualPartCount,
    int OkCount,
    int NgCount,
    int ReworkCount,
    string? LiveResultRaw,
    string? LiveResult,
    IReadOnlyDictionary<string, PlcSignalValue> LivePlcSignals,
    ProductionRecord? CurrentPart,
    IReadOnlyList<LogicalPart> RecentParts,
    string? LastError,
    PlcCommissioningDiagnostic PlcDiagnostic);
