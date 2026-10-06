namespace DynaK.Service.Configuration;

public sealed record PlcDataTypeOption(string Value, string Label);

public sealed class PlcSignalMapping
{
    public const string PartDataReadySignalName = "Part Data Ready";
    public const string OkResultSignalName = "OK";
    public const string NgResultSignalName = "NG";
    public const string CombinedResultSignalName = "OK / NG";
    public const string LegacyCombinedResultSignalName = "OK / NG / Rework";

    public static readonly string[] RequiredSignals =
    [
        "Target Parts Per Shift",
        "Actual Part Count",
        OkResultSignalName,
        NgResultSignalName,
        "Date",
        "Time",
        "Leak Test Value",
        "Auto / Manual",
        "Error",
        "Running Status",
        "QR Code Value",
        "Part Number",
        PartDataReadySignalName,
        "System Ready",
        "Communication OK",
        "Data Saved"
    ];

    public static readonly string[] RequiredReadSignals =
    [
        "Target Parts Per Shift",
        "Actual Part Count",
        OkResultSignalName,
        NgResultSignalName,
        "Date",
        "Time",
        "Leak Test Value",
        "Auto / Manual",
        "Error",
        "Running Status",
        "QR Code Value",
        "Part Number",
        PartDataReadySignalName
    ];

    public static readonly string[] SupportedAddressTypes =
    [
        "",
        "Coil",
        "Discrete Input",
        "Input Register",
        "Holding Register",
        "D Register",
        "M Bit"
    ];

    public static readonly string[] SupportedDataTypes =
    [
        "Bool",
        "Int16",
        "UInt16",
        "Int32",
        "UInt32",
        "Int64",
        "UInt64",
        "Float32",
        "Float64",
        "Word16",
        "DWord32",
        "QWord64",
        "AsciiString",
        "Bcd16",
        "Bcd32"
    ];

    public static readonly PlcDataTypeOption[] SupportedDataTypeOptions =
    [
        new("Bool", "BOOL"),
        new("Int16", "INT16"),
        new("UInt16", "UINT16"),
        new("Int32", "INT32 / DINT"),
        new("UInt32", "UINT32 / DWORD"),
        new("Int64", "INT64 / LINT"),
        new("UInt64", "UINT64 / LWORD"),
        new("Float32", "FLOAT32 / REAL"),
        new("Float64", "FLOAT64 / DOUBLE"),
        new("Word16", "WORD16"),
        new("DWord32", "DWORD32"),
        new("QWord64", "QWORD64"),
        new("AsciiString", "ASCII STRING"),
        new("Bcd16", "BCD16"),
        new("Bcd32", "BCD32")
    ];

    public static readonly string[] SupportedDirections = ["Read", "Write", "ReadWrite"];
    public static readonly string[] SupportedByteOrders = ["", "ABCD", "BADC", "CDAB", "DCBA"];
    public static readonly string[] SupportedWordOrders = ["", "HighLow", "LowHigh"];
    public static readonly string[] SupportedEncodings = ["", "ASCII", "UTF-8"];

    public string SignalName { get; set; } = "";
    public string? Address { get; set; }
    public string AddressType { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Direction { get; set; } = "Read";
    public int? Length { get; set; }
    public decimal ScalingFactor { get; set; } = 1m;
    public decimal Offset { get; set; }
    public string ByteOrder { get; set; } = "";
    public string WordOrder { get; set; } = "";
    public string Encoding { get; set; } = "";
    public string Format { get; set; } = "";
    public string? ValueMap { get; set; }
    public bool Enabled { get; set; }
    public string? Description { get; set; }

    public PlcSignalMapping Clone() => new()
    {
        SignalName = SignalName,
        Address = Address,
        AddressType = AddressType,
        DataType = DataType,
        Direction = Direction,
        Length = Length,
        ScalingFactor = ScalingFactor,
        Offset = Offset,
        ByteOrder = ByteOrder,
        WordOrder = WordOrder,
        Encoding = Encoding,
        Format = Format,
        ValueMap = ValueMap,
        Enabled = Enabled,
        Description = Description
    };

    public static List<PlcSignalMapping> CreateDefaults()
    {
        return
        [
            DRegister("Target Parts Per Shift", "D2000", "UInt16", "Read", "PLC supplied target. Verify datatype during commissioning."),
            DRegister("Actual Part Count", "D2005", "UInt16", "Read", "PLC supplied actual count. Verify datatype during commissioning."),
            DRegister(OkResultSignalName, "D1010", "UInt16", "Read", "PLC OK status. Active value is 1.", valueMap: "1=OK"),
            DRegister(NgResultSignalName, "D1011", "UInt16", "Read", "PLC NG status. Active value is 1.", valueMap: "1=NG"),
            DRegister("Date", "D2015", "AsciiString", "Read", "Provisional ASCII date span. Verify PLC encoding during commissioning.", length: 5, encoding: "ASCII"),
            DRegister("Time", "D1020", "Int16", "Read", "Hour, minute, and second values from D1020-D1022.", length: 3),
            DRegister("Leak Test Value", "D2025", "AsciiString", "Read", "Provisional ASCII decimal leak value. Verify PLC encoding during commissioning.", length: 5, encoding: "ASCII"),
            DRegister("Auto / Manual", "D2030", "UInt16", "Read", "Editable mode mapping. Initial defaults use current known PLC values.", valueMap: "1=Auto;2=Manual"),
            DRegister("Error", "D2035", "UInt16", "Read", "Editable placeholder error-code mapping until real PLC meanings are supplied.", valueMap: "0=No Error;1=Error 1;2=Error 2;3=Error 3;4=Error 4;5=Error 5"),
            DRegister("Running Status", "D2040", "UInt16", "Read", "Editable placeholder running-status mapping until real PLC meanings are supplied.", valueMap: "0=Stopped;1=Running;2=Status 2;3=Status 3;4=Status 4;5=Status 5"),
            DRegister("QR Code Value", "D2050", "AsciiString", "Read", "Verify register length and encoding; length is D registers.", length: 10, encoding: "ASCII"),
            DRegister("Part Number", "D2060", "AsciiString", "Read", "Verify register length and encoding; length is D registers.", length: 10, encoding: "ASCII"),
            DRegister(PartDataReadySignalName, "D1075", "UInt16", "Read", "PLC-controlled read-only trigger. While HIGH, each valid Part Number is captured and stored once without waiting for a LOW transition.", valueMap: "0=LOW;1=HIGH"),
            new PlcSignalMapping
            {
                SignalName = "Serial Number",
                Address = "D2000",
                AddressType = "D Register",
                Direction = "Read",
                ScalingFactor = 1m,
                ByteOrder = "ABCD",
                WordOrder = "HighLow",
                Enabled = false,
                Description = "Serial Number starts at D2000. Configure the real datatype, register range, and encoding before enabling this mapping."
            },
            DRegister("System Ready", "D2100", "UInt16", "Write", "Configurable PC readiness handshake write.", valueMap: "0=OFF;1=ON"),
            DRegister("Communication OK", "D2105", "UInt16", "Write", "Configurable PC communication-health handshake write.", valueMap: "0=OFF;1=ON"),
            DRegister("Data Saved", "D2110", "UInt16", "Write", "Configurable post-persistence acknowledgement write.", valueMap: "0=OFF;1=ON")
        ];
    }

    private static PlcSignalMapping DRegister(
        string signalName,
        string address,
        string dataType,
        string direction,
        string description,
        int length = 1,
        string encoding = "",
        string format = "",
        string? valueMap = null,
        bool enabled = true)
    {
        return new PlcSignalMapping
        {
            SignalName = signalName,
            Address = address,
            AddressType = "D Register",
            DataType = dataType,
            Direction = direction,
            Length = length,
            ScalingFactor = 1m,
            ByteOrder = "ABCD",
            WordOrder = "HighLow",
            Encoding = encoding,
            Format = format,
            ValueMap = valueMap,
            Enabled = enabled,
            Description = description
        };
    }

    public static bool TryCanonicalizeDataType(string? value, out string canonical)
    {
        canonical = "";
        var key = (value ?? "").Replace(" ", "", StringComparison.OrdinalIgnoreCase).Replace("_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        canonical = key switch
        {
            "bool" or "boolean" => "Bool",
            "int16" or "short" => "Int16",
            "uint16" or "ushort" => "UInt16",
            "int32" or "dint" or "int32/dint" => "Int32",
            "uint32" or "uint32/dword" => "UInt32",
            "int64" or "lint" or "int64/lint" => "Int64",
            "uint64" or "lword" or "uint64/lword" => "UInt64",
            "float" or "float32" or "real" or "float32/real" => "Float32",
            "float64" or "double" or "float64/double" => "Float64",
            "word" or "word16" => "Word16",
            "dword" or "dword32" => "DWord32",
            "qword" or "qword64" => "QWord64",
            "asciistring" or "ascii" or "string" => "AsciiString",
            "bcd" or "bcd16" => "Bcd16",
            "bcd32" => "Bcd32",
            _ => ""
        };

        return canonical.Length > 0;
    }

    public static bool IsAsciiString(string? dataType) =>
        TryCanonicalizeDataType(dataType, out var canonical) &&
        canonical.Equals("AsciiString", StringComparison.Ordinal);

    public static string NormalizeSignalName(string? value)
    {
        var signalName = (value ?? "").Trim();
        return signalName.Equals(LegacyCombinedResultSignalName, StringComparison.OrdinalIgnoreCase)
            ? CombinedResultSignalName
            : signalName;
    }

    public static bool IsCombinedResultSignalName(string? value) =>
        NormalizeSignalName(value).Equals(CombinedResultSignalName, StringComparison.OrdinalIgnoreCase);

    public static int RegisterCount(PlcSignalMapping mapping)
    {
        return mapping.Length ?? MinimumRegisterCount(mapping.DataType) ?? 1;
    }

    public static int? MinimumRegisterCount(string? dataType)
    {
        if (!TryCanonicalizeDataType(dataType, out var canonical))
        {
            return null;
        }

        return canonical switch
        {
            "Bool" or "Int16" or "UInt16" or "Word16" or "Bcd16" or "AsciiString" => 1,
            "Int32" or "UInt32" or "DWord32" or "Float32" or "Bcd32" => 2,
            "Int64" or "UInt64" or "QWord64" or "Float64" => 4,
            _ => null
        };
    }

    public static IReadOnlyList<string> GetOperationalValidationErrors(IEnumerable<PlcSignalMapping> configuredMappings)
    {
        var mappingsByName = configuredMappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.SignalName))
            .GroupBy(mapping => NormalizeSignalName(mapping.SignalName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var signalName in RequiredSignals)
        {
            if (!mappingsByName.TryGetValue(signalName, out var matches) || matches.Count == 0)
            {
                errors.Add($"{signalName} mapping is missing.");
                continue;
            }

            if (matches.Count != 1)
            {
                errors.Add($"{signalName} has duplicate mappings.");
                continue;
            }

            var mapping = matches[0];
            if (!mapping.Enabled)
            {
                if (RequiredReadSignals.Contains(signalName, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"{signalName} is disabled.");
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(mapping.Address))
            {
                errors.Add($"{signalName} has no address.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(mapping.AddressType))
            {
                errors.Add($"{signalName} has no address type.");
                continue;
            }

            if (!SupportedAddressTypes.Contains(mapping.AddressType, StringComparer.OrdinalIgnoreCase) ||
                mapping.AddressType.Length == 0)
            {
                errors.Add($"{signalName} address type '{mapping.AddressType}' is invalid.");
                continue;
            }

            if (mapping.AddressType.Equals("D Register", StringComparison.OrdinalIgnoreCase) &&
                !TryParseDRegister(mapping.Address, out _))
            {
                errors.Add($"{signalName} address '{mapping.Address}' is invalid.");
                continue;
            }

            if (!TryCanonicalizeDataType(mapping.DataType, out var dataType))
            {
                errors.Add($"{signalName} data type '{mapping.DataType}' is invalid.");
                continue;
            }

            if (mapping.Length is < 1 || IsAsciiString(dataType) && mapping.Length is null)
            {
                errors.Add($"{signalName} requires a register length.");
                continue;
            }

            if (RequiredReadSignals.Contains(signalName, StringComparer.OrdinalIgnoreCase) && !CanRead(mapping))
            {
                errors.Add($"{signalName} must permit PLC reads.");
                continue;
            }

            if (!RequiredReadSignals.Contains(signalName, StringComparer.OrdinalIgnoreCase) && !CanWrite(mapping))
            {
                errors.Add($"{signalName} must permit PLC writes.");
            }
        }

        return errors;
    }

    private static bool TryParseDRegister(string value, out int address)
    {
        var text = value.Trim();
        if (text.Length > 0 && char.ToUpperInvariant(text[0]) == 'D')
        {
            text = text[1..];
        }

        return int.TryParse(text, out address) && address is >= 0 and <= ushort.MaxValue;
    }

    private static bool CanRead(PlcSignalMapping mapping) =>
        mapping.Direction.Equals("Read", StringComparison.OrdinalIgnoreCase) ||
        mapping.Direction.Equals("ReadWrite", StringComparison.OrdinalIgnoreCase);

    private static bool CanWrite(PlcSignalMapping mapping) =>
        mapping.Direction.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
        mapping.Direction.Equals("ReadWrite", StringComparison.OrdinalIgnoreCase);
}
