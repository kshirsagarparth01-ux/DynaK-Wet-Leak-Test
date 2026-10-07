using System.Text.Json;
using System.Net;
using System.Globalization;
using DynaK.Service.Configuration;
using DynaK.Service.Plc;
using Microsoft.Extensions.Options;

namespace DynaK.Service.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ObsoletePrompt3Signals = new(StringComparer.OrdinalIgnoreCase)
    {
        "Result Ready",
        "PLC Sequence / Cycle ID",
        "QR Code",
        "Leak Lower Limit",
        "Leak Upper Limit",
        PlcSignalMapping.CombinedResultSignalName,
        PlcSignalMapping.LegacyCombinedResultSignalName,
        "Rework",
        "Error Code",
        "Machine Running Status",
        "PC Acknowledge"
    };

    private readonly object _gate = new();
    private readonly ILogger<SettingsStore> _logger;
    private AppSettings _settings;

    public SettingsStore(IOptions<AppSettings> options, ILogger<SettingsStore> logger)
    {
        _settings = options.Value.Clone();
        NormalizeAppSettings(_settings);
        Validate(_settings);
        _logger = logger;
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _settings.Clone();
            }
        }
    }

    public AppSettings BuildCandidate(SettingsUpdate update)
    {
        lock (_gate)
        {
            var candidate = _settings.Clone();

            if (update.StationName is not null)
            {
                candidate.StationName = update.StationName.Trim();
            }

            if (update.LeakTestUnit is not null)
            {
                candidate.LeakTestUnit = update.LeakTestUnit.Trim();
            }

            if (update.DatabasePath is not null)
            {
                candidate.DatabasePath = update.DatabasePath.Trim();
            }

            if (update.LiveLeakValueFilePath is not null)
            {
                candidate.LiveLeakValueFilePath = update.LiveLeakValueFilePath.Trim();
            }

            if (update.LowerLimit.HasValue)
            {
                candidate.LowerLimit = update.LowerLimit.Value;
            }

            if (update.UpperLimit.HasValue)
            {
                candidate.UpperLimit = update.UpperLimit.Value;
            }

            if (update.ReportRootFolder is not null)
            {
                candidate.ReportRootFolder = update.ReportRootFolder.Trim();
            }

            if (update.AutomaticDailyExportEnabled.HasValue)
            {
                candidate.AutomaticDailyExportEnabled = update.AutomaticDailyExportEnabled.Value;
            }

            if (update.Plc is not null)
            {
                candidate.Plc = Normalize(update.Plc);
            }

            if (update.Shifts is not null)
            {
                candidate.Shifts = update.Shifts
                    .Select(s => new ShiftDefinition((s.Name ?? "").Trim(), s.StartsAt, s.EndsAt))
                    .ToList();
            }

            if (update.SignalMappings is not null)
            {
                candidate.SignalMappings = NormalizeMappings(update.SignalMappings);
            }

            if (update.LocalLogs is not null)
            {
                candidate.LocalLogs = update.LocalLogs.Clone();
            }

            Validate(candidate);
            return candidate.Clone();
        }
    }

    public void Activate(AppSettings settings)
    {
        var applied = settings.Clone();
        NormalizeAppSettings(applied);
        Validate(applied);

        lock (_gate)
        {
            _settings = applied.Clone();
        }

        _logger.LogInformation("Settings activated: {Settings}", JsonSerializer.Serialize(applied, JsonOptions));
    }

    public void Apply(SettingsUpdate update)
    {
        Activate(BuildCandidate(update));
    }

    public void ApplyPersisted(IReadOnlyDictionary<string, string> values)
    {
        lock (_gate)
        {
            if (values.TryGetValue("app_settings_json", out var json) && !string.IsNullOrWhiteSpace(json))
            {
                try
                {
                var persisted = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (persisted is not null)
                {
                    persisted = MergeWithDefaults(_settings, persisted);
                    NormalizeAppSettings(persisted);
                    Validate(persisted);
                    _settings = persisted.Clone();
                        return;
                    }
                }
                catch (Exception ex) when (ex is JsonException or SettingsValidationException)
                {
                    _logger.LogError(ex, "Persisted settings are invalid; keeping configured defaults.");
                }
            }

            if (values.TryGetValue("station_name", out var stationName) && !string.IsNullOrWhiteSpace(stationName))
            {
                _settings.StationName = stationName.Trim();
            }

            if (values.TryGetValue("leak_test_unit", out var unit) && !string.IsNullOrWhiteSpace(unit))
            {
                _settings.LeakTestUnit = unit.Trim();
            }

            NormalizeAppSettings(_settings);
            Validate(_settings);
        }
    }

    public static void Validate(AppSettings settings)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.StationId))
        {
            errors.Add("Station ID is required.");
        }

        if (string.IsNullOrWhiteSpace(settings.StationName))
        {
            errors.Add("Station name is required.");
        }

        if (string.IsNullOrWhiteSpace(settings.LeakTestUnit))
        {
            errors.Add("Leak test unit is required.");
        }

        if (string.IsNullOrWhiteSpace(settings.DatabasePath))
        {
            errors.Add("Database location is required.");
        }

        else
        {
            try
            {
                var databasePath = StationDataPaths.ResolveMachinePath(settings.DatabasePath);
                if (string.IsNullOrWhiteSpace(Path.GetFileName(databasePath)))
                {
                    errors.Add("Database location must include a file name.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add($"Database location is invalid: {ex.Message}");
            }
        }

        ValidateLiveLeakValuePath(settings.LiveLeakValueFilePath, errors);
        ValidateReportRootFolder(settings.ReportRootFolder, errors);

        if (settings.LowerLimit >= settings.UpperLimit)
        {
            errors.Add("Leak OK minimum must be less than Leak OK maximum.");
        }

        ValidatePlc(settings.Plc, errors);
        ValidateShifts(settings.Shifts, errors);
        ValidateMappings(settings.SignalMappings, errors);
        ValidateLocalLogs(settings.LocalLogs, errors);

        if (errors.Count > 0)
        {
            throw new SettingsValidationException(errors);
        }
    }
//modified manually using GPT
    private static void NormalizeAppSettings(AppSettings settings)
    {
        settings.LeakTestUnit = string.Equals(
            settings.LeakTestUnit?.Trim(),
            "bar",
            StringComparison.OrdinalIgnoreCase)
                ? "LPM"
                : settings.LeakTestUnit?.Trim() ?? "";

        settings.Plc = Normalize(settings.Plc ?? new PlcConnectionSettings());
        settings.Shifts = settings.Shifts is { Count: > 0 }
            ? settings.Shifts.Select(s => new ShiftDefinition((s.Name ?? "").Trim(), s.StartsAt, s.EndsAt)).ToList()
            : AppSettings.CreateDefaultShifts();
        settings.SignalMappings = MergeMappings(
            PlcSignalMapping.CreateDefaults(),
            NormalizeMappings(settings.SignalMappings ?? []));
        settings.ReportRootFolder = settings.ReportRootFolder?.Trim() ?? "";
        settings.LocalLogs ??= new LocalLogSettings();
    }

    private static PlcConnectionSettings Normalize(PlcConnectionSettings settings)
    {
        return new PlcConnectionSettings
        {
            IpAddress = string.IsNullOrWhiteSpace(settings.IpAddress) ? null : settings.IpAddress.Trim(),
            Port = settings.Port,
            UnitId = settings.UnitId,
            DRegisterModbusOffset = settings.DRegisterModbusOffset,
            PollingIntervalMs = settings.PollingIntervalMs,
            ReconnectIntervalMs = settings.ReconnectIntervalMs,
            ConnectionTimeoutMs = settings.ConnectionTimeoutMs
        };
    }

    private static PlcSignalMapping Normalize(PlcSignalMapping mapping)
    {
        var dataType = (mapping.DataType ?? "").Trim();
        if (PlcSignalMapping.TryCanonicalizeDataType(dataType, out var canonicalDataType))
        {
            dataType = canonicalDataType;
        }

        var length = mapping.Length ?? PlcSignalMapping.MinimumRegisterCount(dataType) ?? 1;

        var signalName = PlcSignalMapping.NormalizeSignalName(mapping.SignalName);
        var valueMap = mapping.ValueMap is null ? null : mapping.ValueMap.Trim();
        if (signalName.Equals(PlcSignalMapping.CombinedResultSignalName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(valueMap, "1=OK;2=NG / Rework", StringComparison.OrdinalIgnoreCase))
        {
            valueMap = "1=OK;2=NG";
        }

        return new PlcSignalMapping
        {
            SignalName = signalName,
            Address = string.IsNullOrWhiteSpace(mapping.Address) ? null : mapping.Address.Trim(),
            AddressType = (mapping.AddressType ?? "").Trim(),
            DataType = dataType,
            Direction = string.IsNullOrWhiteSpace(mapping.Direction) ? "Read" : mapping.Direction.Trim(),
            Length = length,
            ScalingFactor = mapping.ScalingFactor,
            Offset = mapping.Offset,
            ByteOrder = (mapping.ByteOrder ?? "").Trim(),
            WordOrder = (mapping.WordOrder ?? "").Trim(),
            Encoding = dataType.Equals("AsciiString", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(mapping.Encoding)
                ? "ASCII"
                : (mapping.Encoding ?? "").Trim(),
            Format = (mapping.Format ?? "").Trim(),
            ValueMap = valueMap,
            Enabled = mapping.Enabled,
            Description = string.IsNullOrWhiteSpace(mapping.Description) ? null : mapping.Description.Trim()
        };
    }

    private static List<PlcSignalMapping> NormalizeMappings(IEnumerable<PlcSignalMapping> mappings)
    {
        var normalized = mappings.Select(Normalize).ToList();
        var hasOk = normalized.Any(mapping =>
            mapping.SignalName.Equals(PlcSignalMapping.OkResultSignalName, StringComparison.OrdinalIgnoreCase));
        var hasNg = normalized.Any(mapping =>
            mapping.SignalName.Equals(PlcSignalMapping.NgResultSignalName, StringComparison.OrdinalIgnoreCase));
        var result = new List<PlcSignalMapping>(normalized.Count + 1);

        foreach (var mapping in normalized)
        {
            if (!PlcSignalMapping.IsCombinedResultSignalName(mapping.SignalName))
            {
                result.Add(mapping);
                continue;
            }

            if (!hasOk)
            {
                result.Add(CreateSplitResultMapping(
                    mapping,
                    PlcSignalMapping.OkResultSignalName,
                    "D1010",
                    "1=OK",
                    "PLC OK status. Active value is 1."));
                hasOk = true;
            }
            if (!hasNg)
            {
                result.Add(CreateSplitResultMapping(
                    mapping,
                    PlcSignalMapping.NgResultSignalName,
                    "D1011",
                    "1=NG",
                    "PLC NG status. Active value is 1."));
                hasNg = true;
            }
        }

        return result;
    }

    private static PlcSignalMapping CreateSplitResultMapping(
        PlcSignalMapping combined,
        string signalName,
        string address,
        string valueMap,
        string description)
    {
        var mapping = combined.Clone();
        mapping.SignalName = signalName;
        mapping.Address = address;
        mapping.AddressType = "D Register";
        mapping.Length = 1;
        mapping.ValueMap = valueMap;
        mapping.Description = description;
        return mapping;
    }

    private static void ValidatePlc(PlcConnectionSettings plc, List<string> errors)
    {
        var hasIp = !string.IsNullOrWhiteSpace(plc.IpAddress);

        if (hasIp && !IPAddress.TryParse(plc.IpAddress, out _))
        {
            errors.Add("PLC IP address is not valid.");
        }

        if (hasIp && plc.Port is null)
        {
            errors.Add("PLC port is required when PLC IP address is set.");
        }

        if (!hasIp && plc.Port is not null)
        {
            errors.Add("PLC IP address is required when PLC port is set.");
        }

        if (plc.Port is not null and (< 1 or > 65535))
        {
            errors.Add("PLC port must be between 1 and 65535.");
        }

        if (plc.UnitId is not null and (< 0 or > 247))
        {
            errors.Add("Modbus unit/slave ID must be between 0 and 247.");
        }

        if (plc.DRegisterModbusOffset is < -65535 or > 65535)
        {
            errors.Add("D-register Modbus offset must be between -65535 and 65535.");
        }

        if (plc.PollingIntervalMs is < 100 or > 60000)
        {
            errors.Add("Polling interval must be between 100 and 60000 ms.");
        }

        if (plc.ReconnectIntervalMs is < 100 or > 300000)
        {
            errors.Add("Reconnect interval must be between 100 and 300000 ms.");
        }

        if (plc.ConnectionTimeoutMs is < 100 or > 60000)
        {
            errors.Add("Connection timeout must be between 100 and 60000 ms.");
        }
    }

    private static void ValidateShifts(IReadOnlyList<ShiftDefinition> shifts, List<string> errors)
    {
        if (shifts.Count == 0)
        {
            errors.Add("At least one shift is required.");
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shift in shifts)
        {
            if (string.IsNullOrWhiteSpace(shift.Name))
            {
                errors.Add("Shift name is required.");
            }
            else if (!names.Add(shift.Name.Trim()))
            {
                errors.Add($"Duplicate shift name '{shift.Name}'.");
            }

        }
    }

    private static void ValidateMappings(IReadOnlyList<PlcSignalMapping> mappings, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(PlcSignalMapping.RequiredSignals, StringComparer.OrdinalIgnoreCase);
        var configuredAddresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var addressSpans = new List<AddressSpan>();

        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.SignalName))
            {
                errors.Add("PLC mapping signal name is required.");
                continue;
            }

            var signalName = mapping.SignalName.Trim();
            if (!names.Add(signalName))
            {
                errors.Add($"Duplicate PLC mapping signal name '{signalName}'.");
            }

            AddUnsupportedError(mapping.AddressType, PlcSignalMapping.SupportedAddressTypes, signalName, "address type", errors);
            AddUnsupportedError(mapping.DataType, PlcSignalMapping.SupportedDataTypes, signalName, "data type", errors);
            AddUnsupportedError(mapping.Direction, PlcSignalMapping.SupportedDirections, signalName, "read/write direction", errors);
            AddUnsupportedError(mapping.ByteOrder, PlcSignalMapping.SupportedByteOrders, signalName, "byte order", errors);
            AddUnsupportedError(mapping.WordOrder, PlcSignalMapping.SupportedWordOrders, signalName, "word order", errors);
            AddUnsupportedError(mapping.Encoding, PlcSignalMapping.SupportedEncodings, signalName, "encoding", errors);
            ValidateValueMap(mapping, signalName, errors);

            var registerCount = mapping.Length ?? PlcSignalMapping.RegisterCount(mapping);

            if (registerCount < 1)
            {
                errors.Add($"PLC mapping '{signalName}' register count must be greater than zero.");
            }
            if (!mapping.Enabled)
            {
                continue;
            }

            if (CanWrite(mapping) &&
                !PlcSafety.IsAllowedWrite(mapping.SignalName, mapping.Address, mapping.AddressType, PlcSignalMapping.RegisterCount(mapping)))
            {
                errors.Add($"PLC mapping '{signalName}' writes are forbidden. Only the configured System Ready, Communication OK, and Data Saved signals may write to the PLC.");
            }

            if (CanWrite(mapping) && !IsWritableAddressType(mapping.AddressType))
            {
                errors.Add($"PLC mapping '{signalName}' cannot write to address type '{mapping.AddressType}'. Use D Register, Holding Register, Coil, or M Bit.");
            }

            if (string.IsNullOrWhiteSpace(mapping.Address) ||
                string.IsNullOrWhiteSpace(mapping.AddressType) ||
                string.IsNullOrWhiteSpace(mapping.DataType) ||
                string.IsNullOrWhiteSpace(mapping.Direction))
            {
                errors.Add($"Enabled PLC mapping '{signalName}' requires address, address type, data type, and read/write direction.");
            }

            if (PlcSafety.IsAllowedWrite(mapping.SignalName, mapping.Address, mapping.AddressType, PlcSignalMapping.RegisterCount(mapping)))
            {
                ValidateHandshakeValueMap(mapping, signalName, errors);
            }

            if (signalName.Equals(PlcSignalMapping.PartDataReadySignalName, StringComparison.OrdinalIgnoreCase))
            {
                if (!mapping.Direction.Equals("Read", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("PLC mapping 'Part Data Ready' must remain read-only.");
                }
                ValidatePartDataReadyValueMap(mapping, errors);
            }

            if (!string.IsNullOrWhiteSpace(mapping.Address) && !string.IsNullOrWhiteSpace(mapping.AddressType))
            {
                if (!TryParseConfiguredAddress(mapping.AddressType, mapping.Address, out var start))
                {
                    errors.Add($"PLC mapping '{signalName}' address '{mapping.Address}' is not valid for address type '{mapping.AddressType}'.");
                    continue;
                }

                var key = $"{mapping.AddressType.Trim()}|{start}";
                if (configuredAddresses.TryGetValue(key, out var otherSignal))
                {
                    errors.Add($"PLC mapping '{signalName}' conflicts with '{otherSignal}' at {mapping.AddressType} {mapping.Address}.");
                }
                else
                {
                    configuredAddresses[key] = signalName;
                }

                var effectiveCount = PlcSignalMapping.RegisterCount(mapping);
                if (IsBitAddressType(mapping.AddressType) && effectiveCount != 1)
                {
                    errors.Add($"PLC mapping '{signalName}' address type '{mapping.AddressType}' supports exactly one bit, not {effectiveCount} registers.");
                }

                var end = start + effectiveCount - 1;
                if (end > ushort.MaxValue)
                {
                    errors.Add($"PLC mapping '{signalName}' range {FormatRange(mapping.AddressType, start, end)} is outside the supported PLC address range.");
                }

                addressSpans.Add(new AddressSpan(signalName, mapping.AddressType.Trim(), start, end));
            }
        }

        foreach (var addressTypeGroup in addressSpans.GroupBy(span => span.AddressType, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = addressTypeGroup.OrderBy(span => span.Start).ThenBy(span => span.End).ToList();
            for (var index = 0; index < ordered.Count - 1; index++)
            {
                var current = ordered[index];
                var next = ordered[index + 1];
                if (current.End < next.Start)
                {
                    continue;
                }

                var maximum = next.Start - 1;
                var maximumText = maximum >= current.Start
                    ? $" Maximum allowed range: {FormatRange(current.AddressType, current.Start, maximum)}."
                    : " Move one of the start addresses so the ranges are distinct.";
                errors.Add($"PLC mapping '{current.SignalName}' {FormatRange(current.AddressType, current.Start, current.End)} overlaps '{next.SignalName}' {FormatRange(next.AddressType, next.Start, next.End)}.{maximumText}");
            }
        }

        foreach (var signal in required)
        {
            if (!names.Contains(signal))
            {
                errors.Add($"PLC mapping '{signal}' is required.");
            }
        }
    }

    private static void ValidateValueMap(PlcSignalMapping mapping, string signalName, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in PlcValueMap.Parse(mapping.ValueMap))
        {
            if (string.IsNullOrWhiteSpace(entry.PlcValue) || string.IsNullOrWhiteSpace(entry.Meaning))
            {
                errors.Add($"PLC mapping '{signalName}' value map entries must use 'PLC value=Meaning'.");
                continue;
            }

            if (!seen.Add(entry.PlcValue))
            {
                errors.Add($"PLC mapping '{signalName}' has duplicate value map PLC value '{entry.PlcValue}'.");
            }
        }
    }

    private static void ValidateHandshakeValueMap(PlcSignalMapping mapping, string signalName, List<string> errors)
    {
        var entries = PlcValueMap.Parse(mapping.ValueMap);
        if (!entries.Any(entry => entry.Meaning.Equals("ON", StringComparison.OrdinalIgnoreCase)) ||
            !entries.Any(entry => entry.Meaning.Equals("OFF", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"PLC mapping '{signalName}' requires distinct ON and OFF value-map entries, for example '0=OFF;1=ON'.");
        }
    }

    private static void ValidatePartDataReadyValueMap(PlcSignalMapping mapping, List<string> errors)
    {
        var entries = PlcValueMap.Parse(mapping.ValueMap);
        if (!entries.Any(entry => entry.Meaning.Equals("HIGH", StringComparison.OrdinalIgnoreCase)) ||
            !entries.Any(entry => entry.Meaning.Equals("LOW", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("PLC mapping 'Part Data Ready' requires distinct HIGH and LOW value-map entries, for example '0=LOW;1=HIGH'.");
        }
    }

    private static void ValidateLiveLeakValuePath(string configuredPath, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            errors.Add("Live leak value text-file location is required.");
            return;
        }

        try
        {
            var path = StationDataPaths.ResolveMachinePath(configuredPath);
            var fileName = Path.GetFileName(path);
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(directory))
            {
                errors.Add("Live leak value text-file location must include a file name and parent folder.");
                return;
            }

            if (!Directory.Exists(directory))
            {
                return;
            }

            if (File.Exists(path))
            {
                using var existing = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            }

            var probePath = Path.Combine(directory, $".dynak-live-value-write-test-{Guid.NewGuid():N}.tmp");
            using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"Live leak value text-file location is invalid or not writable: {ex.Message}");
        }
    }

    private static void ValidateReportRootFolder(string configuredPath, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            errors.Add("Report root folder is required.");
            return;
        }

        try
        {
            if (!Path.IsPathRooted(configuredPath) ||
                string.IsNullOrWhiteSpace(Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredPath)))))
            {
                errors.Add("Report root folder must be an absolute folder path.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"Report root folder is invalid: {ex.Message}");
        }
    }

    private static void ValidateLocalLogs(LocalLogSettings settings, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            errors.Add("Local log path is required.");
        }

        if (settings.MaxFileBytes is < 65536 or > 104857600)
        {
            errors.Add("Local log max file size must be between 64 KB and 100 MB.");
        }

        if (settings.RetainedFileCount is < 1 or > 50)
        {
            errors.Add("Local log retained file count must be between 1 and 50.");
        }
    }

    private static void AddUnsupportedError(string value, IReadOnlyCollection<string> supported, string signalName, string fieldName, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(value) && !supported.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"PLC mapping '{signalName}' has unsupported {fieldName} '{value}'.");
        }
    }

    private static bool TryParseConfiguredAddress(string addressType, string value, out int address)
    {
        var text = value.Trim();
        if (addressType.Equals("D Register", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Length > 0 && char.ToUpperInvariant(text[0]) == 'D')
            {
                text = text[1..];
            }

            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out address) &&
                address is >= 0 and <= ushort.MaxValue;
        }

        address = 0;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var reference))
        {
            return false;
        }

        var referenceBase = addressType.ToUpperInvariant() switch
        {
            "HOLDING REGISTER" => 40001,
            "INPUT REGISTER" => 30001,
            "DISCRETE INPUT" => 10001,
            "COIL" or "M BIT" => 1,
            _ => -1
        };
        if (referenceBase < 0)
        {
            return false;
        }

        address = reference >= referenceBase ? reference - referenceBase : reference;
        return address is >= 0 and <= ushort.MaxValue;
    }

    private static bool IsWritableAddressType(string addressType) =>
        addressType.Equals("D Register", StringComparison.OrdinalIgnoreCase) ||
        addressType.Equals("Holding Register", StringComparison.OrdinalIgnoreCase) ||
        addressType.Equals("Coil", StringComparison.OrdinalIgnoreCase) ||
        addressType.Equals("M Bit", StringComparison.OrdinalIgnoreCase);

    private static bool IsBitAddressType(string addressType) =>
        addressType.Equals("Coil", StringComparison.OrdinalIgnoreCase) ||
        addressType.Equals("Discrete Input", StringComparison.OrdinalIgnoreCase) ||
        addressType.Equals("M Bit", StringComparison.OrdinalIgnoreCase);

    private static string FormatRange(string addressType, int start, int end)
    {
        var prefix = addressType.Equals("D Register", StringComparison.OrdinalIgnoreCase) ? "D" : $"{addressType} ";
        return start == end ? $"{prefix}{start}" : $"{prefix}{start}-{prefix}{end}";
    }

    private static bool CanRead(PlcSignalMapping mapping) =>
        mapping.Direction.Equals("Read", StringComparison.OrdinalIgnoreCase) ||
        mapping.Direction.Equals("ReadWrite", StringComparison.OrdinalIgnoreCase);

    private static bool CanWrite(PlcSignalMapping mapping) =>
        mapping.Direction.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
        mapping.Direction.Equals("ReadWrite", StringComparison.OrdinalIgnoreCase);

    private sealed record AddressSpan(string SignalName, string AddressType, int Start, int End);

    private static AppSettings MergeWithDefaults(AppSettings defaults, AppSettings persisted)
    {
        var merged = defaults.Clone();
        merged.StationId = string.IsNullOrWhiteSpace(persisted.StationId) ? merged.StationId : persisted.StationId;
        merged.StationName = string.IsNullOrWhiteSpace(persisted.StationName) ? merged.StationName : persisted.StationName;
        // Legacy database snapshots never owned the deployment database path.
        // New machine-file settings are bound before this store is created and do not use this merge path.
        merged.DatabasePath = defaults.DatabasePath;
        merged.LiveLeakValueFilePath = string.IsNullOrWhiteSpace(persisted.LiveLeakValueFilePath)
            ? defaults.LiveLeakValueFilePath
            : persisted.LiveLeakValueFilePath;
        merged.LeakTestUnit = string.IsNullOrWhiteSpace(persisted.LeakTestUnit) ? merged.LeakTestUnit : persisted.LeakTestUnit;
        merged.LowerLimit = persisted.LowerLimit;
        merged.UpperLimit = persisted.UpperLimit;
        merged.ReportRootFolder = string.IsNullOrWhiteSpace(persisted.ReportRootFolder)
            ? defaults.ReportRootFolder
            : persisted.ReportRootFolder;
        merged.AutomaticDailyExportEnabled = persisted.AutomaticDailyExportEnabled;
        merged.Plc = MergePlc(defaults.Plc, persisted.Plc);
        merged.Shifts = persisted.Shifts.Count == 0 ? defaults.Shifts.ToList() : persisted.Shifts.ToList();
        merged.SignalMappings = MergeMappings(defaults.SignalMappings, persisted.SignalMappings);
        merged.LocalLogs = persisted.LocalLogs ?? defaults.LocalLogs.Clone();
        return merged;
    }

    private static PlcConnectionSettings MergePlc(PlcConnectionSettings defaults, PlcConnectionSettings persisted)
    {
        return new PlcConnectionSettings
        {
            IpAddress = string.IsNullOrWhiteSpace(persisted.IpAddress) ? defaults.IpAddress : persisted.IpAddress,
            Port = persisted.Port ?? defaults.Port,
            UnitId = persisted.UnitId ?? defaults.UnitId,
            DRegisterModbusOffset = persisted.DRegisterModbusOffset,
            PollingIntervalMs = persisted.PollingIntervalMs == 0 ? defaults.PollingIntervalMs : persisted.PollingIntervalMs,
            ReconnectIntervalMs = persisted.ReconnectIntervalMs == 0 ? defaults.ReconnectIntervalMs : persisted.ReconnectIntervalMs,
            ConnectionTimeoutMs = persisted.ConnectionTimeoutMs == 0 ? defaults.ConnectionTimeoutMs : persisted.ConnectionTimeoutMs
        };
    }

    private static List<PlcSignalMapping> MergeMappings(IReadOnlyList<PlcSignalMapping> defaults, IReadOnlyList<PlcSignalMapping> persisted)
    {
        if (persisted.Count == 0)
        {
            return defaults.Select(mapping => mapping.Clone()).ToList();
        }

        var byName = persisted
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.SignalName))
            .GroupBy(mapping => PlcSignalMapping.NormalizeSignalName(mapping.SignalName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var merged = new List<PlcSignalMapping>();
        var defaultNames = defaults
            .Select(mapping => PlcSignalMapping.NormalizeSignalName(mapping.SignalName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var defaultMapping in defaults)
        {
            merged.Add(byName.TryGetValue(defaultMapping.SignalName, out var saved)
                ? MergeMapping(defaultMapping, saved)
                : defaultMapping.Clone());
        }

        foreach (var saved in persisted.Where(mapping =>
            !string.IsNullOrWhiteSpace(mapping.SignalName) &&
            !defaultNames.Contains(PlcSignalMapping.NormalizeSignalName(mapping.SignalName)) &&
            !ObsoletePrompt3Signals.Contains(mapping.SignalName)))
        {
            merged.Add(saved.Clone());
        }

        return merged;
    }

    private static PlcSignalMapping MergeMapping(PlcSignalMapping defaults, PlcSignalMapping persisted)
    {
        var merged = defaults.Clone();
        merged.Address = string.IsNullOrWhiteSpace(persisted.Address) ? merged.Address : persisted.Address;
        merged.AddressType = string.IsNullOrWhiteSpace(persisted.AddressType) ? merged.AddressType : persisted.AddressType;
        merged.DataType = string.IsNullOrWhiteSpace(persisted.DataType) ? merged.DataType : persisted.DataType;
        merged.Direction = string.IsNullOrWhiteSpace(persisted.Direction) ? merged.Direction : persisted.Direction;
        merged.Length = persisted.Length ?? merged.Length;
        merged.ScalingFactor = persisted.ScalingFactor == 0m ? merged.ScalingFactor : persisted.ScalingFactor;
        merged.Offset = persisted.Offset;
        merged.ByteOrder = string.IsNullOrWhiteSpace(persisted.ByteOrder) ? merged.ByteOrder : persisted.ByteOrder;
        merged.WordOrder = string.IsNullOrWhiteSpace(persisted.WordOrder) ? merged.WordOrder : persisted.WordOrder;
        merged.Encoding = string.IsNullOrWhiteSpace(persisted.Encoding) ? merged.Encoding : persisted.Encoding;
        merged.Format = string.IsNullOrWhiteSpace(persisted.Format) ? merged.Format : persisted.Format;
        merged.ValueMap = persisted.ValueMap is null ? merged.ValueMap : persisted.ValueMap.Trim();
        var persistedWasConfigured = persisted.Enabled ||
            !string.IsNullOrWhiteSpace(persisted.Address) ||
            !string.IsNullOrWhiteSpace(persisted.AddressType) ||
            !string.IsNullOrWhiteSpace(persisted.DataType);
        merged.Enabled = persistedWasConfigured ? persisted.Enabled : defaults.Enabled;
        merged.Description = string.IsNullOrWhiteSpace(persisted.Description) ? merged.Description : persisted.Description;
        return merged;
    }
}

public sealed record SettingsUpdate(
    string? StationName,
    string? DatabasePath,
    string? LeakTestUnit,
    PlcConnectionSettings? Plc,
    IReadOnlyList<ShiftDefinition>? Shifts,
    IReadOnlyList<PlcSignalMapping>? SignalMappings,
    LocalLogSettings? LocalLogs,
    string? LiveLeakValueFilePath = null,
    decimal? LowerLimit = null,
    decimal? UpperLimit = null,
    string? ReportRootFolder = null,
    bool? AutomaticDailyExportEnabled = null);

public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(IReadOnlyList<string> errors)
        : base(string.Join(" ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
