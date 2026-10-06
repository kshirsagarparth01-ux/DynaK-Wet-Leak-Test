using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using DynaK.Service.Configuration;
using DynaK.Service.Models;

namespace DynaK.Service.Plc;

public class MitsubishiModbusPlcClient : IPlcClient
{
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly ILogger<MitsubishiModbusPlcClient> _logger;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private PlcClientConfiguration? _configuration;
    private ushort _transactionId;
    private bool _disposed;
    private readonly Dictionary<string, string> _signalDecodeErrors = new(StringComparer.OrdinalIgnoreCase);

    public MitsubishiModbusPlcClient(ILogger<MitsubishiModbusPlcClient>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MitsubishiModbusPlcClient>.Instance;
    }

    public async Task ConnectAsync(PlcClientConfiguration configuration, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (!configuration.IsConfigured)
        {
            throw new InvalidOperationException("PLC IP address and port are required.");
        }

        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            CloseConnection();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(configuration.Connection.ConnectionTimeoutMs, 100, 60000)));

            _logger.LogInformation(
                "PLC connecting to {IpAddress}:{Port} using Modbus TCP (unit {UnitId}, writes limited to configured handshake signals)",
                configuration.Connection.IpAddress,
                configuration.Connection.Port,
                configuration.Connection.UnitId ?? 1);
            var tcpClient = new TcpClient { NoDelay = true };
            await tcpClient.ConnectAsync(configuration.Connection.IpAddress!, configuration.Connection.Port!.Value, timeout.Token);

            _tcpClient = tcpClient;
            _stream = tcpClient.GetStream();
            _configuration = configuration;
            _logger.LogInformation("PLC TCP connection established to {IpAddress}:{Port}", configuration.Connection.IpAddress, configuration.Connection.Port);
            var partDataReadyMapping = configuration.SignalMappings.FirstOrDefault(mapping =>
                mapping.Enabled &&
                CanRead(mapping) &&
                string.Equals(mapping.SignalName, PlcSignalMapping.PartDataReadySignalName, StringComparison.OrdinalIgnoreCase));
            if (partDataReadyMapping is not null)
            {
                var plan = ResolveReadPlan(configuration, partDataReadyMapping);
                _logger.LogInformation(
                    "Part Data Ready mapping {AddressType} {Address} resolves to Modbus function {FunctionCode}, zero-based start address {StartAddress} (configured D-register offset {DRegisterOffset}).",
                    partDataReadyMapping.AddressType,
                    partDataReadyMapping.Address,
                    plan.FunctionCode,
                    plan.StartAddress,
                    configuration.Connection.DRegisterModbusOffset);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            CloseConnection();
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public Task<bool> IsConnectedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_tcpClient?.Connected == true && _stream is not null);
    }

    public async Task<PlcPollResult> ReadPollAsync(CancellationToken cancellationToken)
    {
        var signals = await ReadConfiguredSignalsAsync(cancellationToken);
        return new PlcPollResult(
            BuildMachineStatus(signals),
            signals);
    }

    public async Task<PlcMachineStatus> ReadMachineStatusAsync(CancellationToken cancellationToken)
    {
        var signals = await ReadConfiguredSignalsAsync(cancellationToken);
        return BuildMachineStatus(signals);
    }

    private static PlcMachineStatus BuildMachineStatus(IReadOnlyDictionary<string, PlcSignalValue> signals)
    {
        var timestamp = DateTimeOffset.Now;
        var runningStatus = ResolveEnumSignal(signals, "Running Status", "Status");
        var error = ResolveEnumSignal(signals, "Error", "Error");
        var running = IsRunningStatus(runningStatus.ResolvedText);
        var errorCode = IsEmptyOrZero(error.RawText) || IsNoError(error.ResolvedText) ? null : error.RawText;

        return new PlcMachineStatus(
            true,
            running,
            errorCode,
            errorCode is null ? null : error.ResolvedText,
            timestamp);
    }

    public async Task<IReadOnlyDictionary<string, PlcSignalValue>> ReadConfiguredSignalsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var configuration = RequireConfiguration();
        var values = new Dictionary<string, PlcSignalValue>(StringComparer.OrdinalIgnoreCase);
        var mappings = new List<SignalReadPlan>();
        foreach (var mapping in configuration.SignalMappings.Where(mapping => mapping.Enabled && CanRead(mapping)))
        {
            try
            {
                mappings.Add(ResolveReadPlan(configuration, mapping));
            }
            catch (PlcSignalMappingException ex)
            {
                values[mapping.SignalName] = UndecodableSignal(mapping, null, ex);
            }
        }

        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            foreach (var block in BuildRegisterBlocks(mappings.Where(plan => plan.IsRegister)))
            {
                var registers = await ReadRegistersAsync(block.FunctionCode, block.StartAddress, block.RegisterCount, cancellationToken);
                foreach (var plan in block.Plans)
                {
                    var offset = plan.StartAddress - block.StartAddress;
                    var slice = registers.Skip(offset).Take(plan.RegisterCount).ToArray();
                    try
                    {
                        values[plan.Mapping.SignalName] = DecodeRegisters(plan.Mapping, slice);
                        _signalDecodeErrors.Remove(plan.Mapping.SignalName);
                    }
                    catch (Exception ex) when (ex is PlcSignalMappingException or FormatException or OverflowException)
                    {
                        values[plan.Mapping.SignalName] = UndecodableSignal(plan.Mapping, slice, ex);
                    }
                }
            }

            foreach (var plan in mappings.Where(plan => !plan.IsRegister))
            {
                values[plan.Mapping.SignalName] = DecodeValue(plan.Mapping, await ReadBitAsync(plan.FunctionCode, plan.StartAddress, cancellationToken));
            }
        }
        finally
        {
            _ioGate.Release();
        }

        return values;
    }

    private PlcSignalValue UndecodableSignal(PlcSignalMapping mapping, IReadOnlyList<ushort>? registers, Exception exception)
    {
        if (!_signalDecodeErrors.TryGetValue(mapping.SignalName, out var previous) ||
            !StringComparer.Ordinal.Equals(previous, exception.Message))
        {
            _logger.LogWarning(exception, "PLC signal {SignalName} could not be decoded; other configured signals will continue updating", mapping.SignalName);
            _signalDecodeErrors[mapping.SignalName] = exception.Message;
        }
        var raw = registers is null ? null : string.Join(",", registers.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        return new PlcSignalValue(raw, null, false, exception.Message);
    }

    public async Task<PartDataSnapshot?> ReadPartDataSnapshotAsync(CancellationToken cancellationToken)
    {
        var signals = await ReadConfiguredSignalsAsync(cancellationToken);
        return BuildPartDataSnapshot(signals);
    }

    private PartDataSnapshot? BuildPartDataSnapshot(IReadOnlyDictionary<string, PlcSignalValue> signals)
    {
        var qrCode = ReadSignalText(signals, "QR Code Value");
        var partNumber = ReadSignalText(signals, "Part Number");
        var serialNumber = ReadSignalText(signals, "Serial Number");

        DateTimeOffset timestamp;
        try
        {
            timestamp = BuildTimestamp(signals);
        }
        catch (PlcSignalMappingException ex)
        {
            timestamp = DateTimeOffset.Now;
            _logger.LogWarning(ex, "PLC Date/Time is unavailable; using the Part Data Ready capture time while preserving the raw PLC values.");
        }

        var configuration = RequireConfiguration();
        var result = PlcResultResolver.Resolve(signals);
        var actualPartCount = ReadSignalInt(signals, "Actual Part Count");
        var targetPartsPerShift = ReadSignalInt(signals, "Target Parts Per Shift");
        var leakValue = ReadSignalDecimal(signals, "Leak Test Value");
        if (leakValue is null)
        {
            _logger.LogWarning(
                "PLC Leak Test Value '{LeakTestValue}' is unavailable; the part will be saved with a null leak value while preserving the raw PLC value.",
                ReadSignalText(signals, "Leak Test Value"));
        }

        var mode = ResolveEnumSignal(signals, "Auto / Manual", "Mode");
        var runningStatus = ResolveEnumSignal(signals, "Running Status", "Status");
        var error = ResolveEnumSignal(signals, "Error", "Error");
        var isAutoMode = IsAutoMode(mode.ResolvedText);
        var isRunning = IsRunningStatus(runningStatus.ResolvedText);

        return new PartDataSnapshot(
            configuration.StationId,
            string.IsNullOrWhiteSpace(serialNumber) ? null : serialNumber,
            qrCode,
            partNumber,
            targetPartsPerShift,
            actualPartCount,
            leakValue,
            configuration.LeakTestUnit,
            configuration.LowerLimit,
            configuration.UpperLimit,
            result.RawText,
            result.ResolvedText,
            mode.RawText,
            mode.ResolvedText,
            isAutoMode,
            isRunning,
            error.RawText,
            error.ResolvedText,
            runningStatus.RawText,
            runningStatus.ResolvedText,
            timestamp,
            signals);
    }

    public async Task WriteConfiguredSignalAsync(string signalName, object? value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var configuration = RequireConfiguration();
        var mapping = configuration.SignalMappings.FirstOrDefault(mapping =>
            mapping.Enabled &&
            CanWrite(mapping) &&
            string.Equals(mapping.SignalName, signalName, StringComparison.OrdinalIgnoreCase));
        if (mapping is null ||
            !PlcSafety.IsAllowedWrite(mapping.SignalName, mapping.Address, mapping.AddressType, RegisterCount(mapping)))
        {
            var address = mapping is null ? "unconfigured" : $"{mapping.AddressType} {mapping.Address}";
            _logger.LogWarning("Blocked PLC write to {SignalName} at {Address}: WRITE FORBIDDEN", signalName, address);
            throw new PlcWriteBlockedException($"{signalName} ({address})");
        }

        var plan = ResolveReadPlan(configuration, mapping);
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            if (plan.IsRegister)
            {
                await WriteRegistersAsync(plan.StartAddress, EncodeRegisters(mapping, value), cancellationToken);
                return;
            }

            await WriteCoilAsync(plan.StartAddress, CoerceBool(ResolveWriteScalar(mapping, value)), cancellationToken);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _ioGate.WaitAsync();
        try
        {
            _disposed = true;
            CloseConnection();
        }
        finally
        {
            _ioGate.Release();
            _ioGate.Dispose();
        }
    }

    private PlcClientConfiguration RequireConfiguration()
    {
        return _configuration ?? throw new InvalidOperationException("PLC client is not connected.");
    }

    private void EnsureConnected()
    {
        if (_tcpClient?.Connected != true || _stream is null)
        {
            throw new IOException("PLC TCP connection is not open.");
        }
    }

    private void CloseConnection()
    {
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _stream = null;
        _tcpClient = null;
        _configuration = null;
    }

    private async Task<ushort[]> ReadRegistersAsync(byte functionCode, int startAddress, int registerCount, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), CheckedUInt16(startAddress, "start address"));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), CheckedUInt16(registerCount, "register count"));

        var response = await ExecuteAsync(functionCode, payload, cancellationToken);
        if (response.Length != 1 + (registerCount * 2) || response[0] != registerCount * 2)
        {
            throw new IOException($"Unexpected Modbus register response length for address {startAddress}.");
        }

        var registers = new ushort[registerCount];
        for (var index = 0; index < registerCount; index++)
        {
            registers[index] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(1 + (index * 2), 2));
        }

        return registers;
    }

    private async Task<bool> ReadBitAsync(byte functionCode, int startAddress, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), CheckedUInt16(startAddress, "start address"));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), 1);

        var response = await ExecuteAsync(functionCode, payload, cancellationToken);
        if (response.Length < 2 || response[0] != 1)
        {
            throw new IOException($"Unexpected Modbus bit response length for address {startAddress}.");
        }

        return (response[1] & 1) == 1;
    }

    private Task WriteRegistersAsync(int startAddress, ushort[] registers, CancellationToken cancellationToken)
    {
        var payload = new byte[5 + (registers.Length * 2)];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), CheckedUInt16(startAddress, "start address"));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), CheckedUInt16(registers.Length, "register count"));
        payload[4] = CheckedByte(registers.Length * 2, "byte count");
        for (var index = 0; index < registers.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(5 + (index * 2), 2), registers[index]);
        }

        return ExecuteAsync(16, payload, cancellationToken);
    }

    private Task WriteCoilAsync(int startAddress, bool value, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), CheckedUInt16(startAddress, "start address"));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), value ? (ushort)0xFF00 : (ushort)0x0000);
        return ExecuteAsync(5, payload, cancellationToken);
    }

    private async Task<byte[]> ExecuteAsync(byte functionCode, byte[] payload, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new IOException("PLC TCP stream is not open.");
        var transactionId = unchecked(++_transactionId);
        var unitId = CheckedByte(RequireConfiguration().Connection.UnitId ?? 1, "unit id");
        var frame = new byte[8 + payload.Length];

        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), CheckedUInt16(2 + payload.Length, "PDU length"));
        frame[6] = unitId;
        frame[7] = functionCode;
        payload.CopyTo(frame.AsSpan(8));

        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var header = new byte[7];
        await ReadExactAsync(stream, header, cancellationToken);
        var responseTransactionId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
        var protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        if (responseTransactionId != transactionId || protocolId != 0 || length < 2)
        {
            throw new IOException("Unexpected Modbus TCP response header.");
        }

        var pdu = new byte[length - 1];
        await ReadExactAsync(stream, pdu, cancellationToken);
        if (pdu[0] == (functionCode | 0x80))
        {
            var code = pdu.Length > 1 ? pdu[1] : 0;
            throw new IOException($"Modbus exception {code} for function {functionCode}.");
        }

        if (pdu[0] != functionCode)
        {
            throw new IOException($"Unexpected Modbus function {pdu[0]} in response.");
        }

        return pdu.Skip(1).ToArray();
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("PLC TCP connection closed.");
            }

            offset += read;
        }
    }

    private static IEnumerable<RegisterBlock> BuildRegisterBlocks(IEnumerable<SignalReadPlan> plans)
    {
        foreach (var group in plans.GroupBy(plan => plan.FunctionCode).OrderBy(group => group.Key))
        {
            RegisterBlock? current = null;
            foreach (var plan in group.OrderBy(plan => plan.StartAddress))
            {
                if (current is null || plan.StartAddress > current.StartAddress + current.RegisterCount)
                {
                    if (current is not null)
                    {
                        yield return current;
                    }

                    current = new RegisterBlock(plan.FunctionCode, plan.StartAddress, plan.RegisterCount);
                }
                else
                {
                    var end = Math.Max(current.StartAddress + current.RegisterCount, plan.StartAddress + plan.RegisterCount);
                    current.RegisterCount = end - current.StartAddress;
                }

                current.Plans.Add(plan);
            }

            if (current is not null)
            {
                yield return current;
            }
        }
    }

    private static SignalReadPlan ResolveReadPlan(PlcClientConfiguration configuration, PlcSignalMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.Address))
        {
            throw new PlcSignalMappingException($"PLC mapping '{mapping.SignalName}' is missing an address.");
        }

        var addressType = mapping.AddressType.Trim();
        if (addressType.Equals("D Register", StringComparison.OrdinalIgnoreCase))
        {
            var dAddress = ParseDeviceAddress(mapping.Address, 'D');
            var modbusAddress = dAddress + configuration.Connection.DRegisterModbusOffset;
            return new SignalReadPlan(mapping, 3, CheckedAddress(modbusAddress, mapping), RegisterCount(mapping), true);
        }

        if (addressType.Equals("Holding Register", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalReadPlan(mapping, 3, ParseModbusReference(mapping.Address, 40001), RegisterCount(mapping), true);
        }

        if (addressType.Equals("Input Register", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalReadPlan(mapping, 4, ParseModbusReference(mapping.Address, 30001), RegisterCount(mapping), true);
        }

        if (addressType.Equals("Coil", StringComparison.OrdinalIgnoreCase) ||
            addressType.Equals("M Bit", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalReadPlan(mapping, 1, ParseModbusReference(mapping.Address, 1), 1, false);
        }

        if (addressType.Equals("Discrete Input", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalReadPlan(mapping, 2, ParseModbusReference(mapping.Address, 10001), 1, false);
        }

        throw new PlcSignalMappingException($"PLC mapping '{mapping.SignalName}' has unsupported address type '{mapping.AddressType}'.");
    }

    private static PlcSignalValue DecodeRegisters(PlcSignalMapping mapping, ushort[] registers)
    {
        var decoded = PlcValueDecoder.Decode(mapping, registers);
        if (mapping.SignalName.Equals("Leak Test Value", StringComparison.OrdinalIgnoreCase) &&
            decoded.Value is string text &&
            decimal.TryParse(new string(text.Where(character => !char.IsWhiteSpace(character)).ToArray()), NumberStyles.Number, CultureInfo.InvariantCulture, out var leakValue))
        {
            return new PlcSignalValue(text, leakValue, false);
        }

        return decoded.Value is decimal numeric
            ? DecodeNumeric(mapping, numeric)
            : DecodeValue(mapping, decoded.Value);
    }

    private static PlcSignalValue DecodeValue(PlcSignalMapping mapping, object? rawValue)
    {
        if (PlcValueMap.TryResolve(mapping.ValueMap, rawValue, out var meaning))
        {
            return new PlcSignalValue(rawValue, meaning, true);
        }

        return new PlcSignalValue(rawValue, rawValue, false);
    }

    private static PlcSignalValue DecodeNumeric(PlcSignalMapping mapping, decimal rawValue)
    {
        if (PlcValueMap.TryResolve(mapping.ValueMap, rawValue, out var meaning))
        {
            return new PlcSignalValue(rawValue, meaning, true);
        }

        return new PlcSignalValue(rawValue, (rawValue * mapping.ScalingFactor) + mapping.Offset, false);
    }

    private static ushort[] EncodeRegisters(PlcSignalMapping mapping, object? value)
    {
        var scalar = ResolveWriteScalar(mapping, value);
        if (!PlcSignalMapping.TryCanonicalizeDataType(mapping.DataType, out var dataType))
        {
            throw new PlcSignalMappingException($"PLC mapping '{mapping.SignalName}' has unsupported data type '{mapping.DataType}'.");
        }

        return dataType switch
        {
            "Bool" => [CoerceBool(scalar) ? (ushort)1 : (ushort)0],
            "Int16" => [unchecked((ushort)Convert.ToInt16(scalar, CultureInfo.InvariantCulture))],
            "UInt16" or "Word16" => [Convert.ToUInt16(scalar, CultureInfo.InvariantCulture)],
            "Int32" => EncodeInt32(Convert.ToInt32(scalar, CultureInfo.InvariantCulture), mapping),
            "UInt32" or "DWord32" => EncodeUInt32(Convert.ToUInt32(scalar, CultureInfo.InvariantCulture), mapping),
            "Int64" => EncodeInt64(Convert.ToInt64(scalar, CultureInfo.InvariantCulture), mapping),
            "UInt64" or "QWord64" => EncodeUInt64(Convert.ToUInt64(scalar, CultureInfo.InvariantCulture), mapping),
            "Float32" => BytesToRegisters(ApplyByteOrder(mapping, BitConverter.GetBytes(Convert.ToSingle(scalar, CultureInfo.InvariantCulture)).Reverse().ToArray())),
            "Float64" => BytesToRegisters(ApplyByteOrder(mapping, BitConverter.GetBytes(Convert.ToDouble(scalar, CultureInfo.InvariantCulture)).Reverse().ToArray())),
            "AsciiString" => EncodeString(mapping, Convert.ToString(scalar, CultureInfo.InvariantCulture) ?? ""),
            "Bcd16" => EncodeBcd(scalar, 1),
            "Bcd32" => EncodeBcd(scalar, 2),
            _ => throw new PlcSignalMappingException($"PLC mapping '{mapping.SignalName}' has unsupported data type '{mapping.DataType}'.")
        };
    }

    private static ushort[] EncodeInt32(int value, PlcSignalMapping mapping)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return BytesToRegisters(ApplyByteOrder(mapping, bytes));
    }

    private static ushort[] EncodeUInt32(uint value, PlcSignalMapping mapping)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return BytesToRegisters(ApplyByteOrder(mapping, bytes));
    }

    private static ushort[] EncodeInt64(long value, PlcSignalMapping mapping)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return BytesToRegisters(ApplyByteOrder(mapping, bytes));
    }

    private static ushort[] EncodeUInt64(ulong value, PlcSignalMapping mapping)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return BytesToRegisters(ApplyByteOrder(mapping, bytes));
    }

    private static byte[] OrderedBytes(PlcSignalMapping mapping, ushort[] registers)
    {
        var ordered = mapping.WordOrder.Equals("LowHigh", StringComparison.OrdinalIgnoreCase) && registers.Length > 1
            ? registers.Reverse().ToArray()
            : registers.ToArray();
        var bytes = new byte[ordered.Length * 2];
        for (var index = 0; index < ordered.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(index * 2, 2), ordered[index]);
        }

        return ApplyByteOrder(mapping, bytes);
    }

    private static byte[] ApplyByteOrder(PlcSignalMapping mapping, byte[] bytes)
    {
        if (bytes.Length < 4)
        {
            return bytes;
        }

        return mapping.ByteOrder.ToUpperInvariant() switch
        {
            "" or "ABCD" => bytes,
            "BADC" => [bytes[1], bytes[0], bytes[3], bytes[2], .. bytes.Skip(4)],
            "CDAB" => [bytes[2], bytes[3], bytes[0], bytes[1], .. bytes.Skip(4)],
            "DCBA" => [bytes[3], bytes[2], bytes[1], bytes[0], .. bytes.Skip(4)],
            _ => bytes
        };
    }

    private static ushort[] EncodeString(PlcSignalMapping mapping, string value)
    {
        var encoding = mapping.Encoding.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8 : Encoding.ASCII;
        var registerCount = mapping.Length ?? Math.Max(1, (encoding.GetByteCount(value) + 1) / 2);
        var bytes = new byte[registerCount * 2];
        encoding.GetBytes(value.AsSpan(), bytes.AsSpan());
        return BytesToRegisters(bytes);
    }

    private static ushort[] BytesToRegisters(byte[] bytes)
    {
        var registers = new ushort[(bytes.Length + 1) / 2];
        for (var index = 0; index < registers.Length; index++)
        {
            var high = bytes[index * 2];
            var low = index * 2 + 1 < bytes.Length ? bytes[index * 2 + 1] : (byte)0;
            registers[index] = (ushort)((high << 8) | low);
        }

        return registers;
    }

    private static ushort[] EncodeBcd(object? value, int registerCount)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            digits = "0";
        }

        var width = registerCount * 4;
        if (digits.Length > width)
        {
            throw new PlcSignalMappingException($"BCD value '{text}' needs more than {registerCount} register(s).");
        }

        digits = digits.PadLeft(width, '0');
        var registers = new ushort[registerCount];
        for (var registerIndex = 0; registerIndex < registerCount; registerIndex++)
        {
            ushort register = 0;
            for (var digitIndex = 0; digitIndex < 4; digitIndex++)
            {
                register = (ushort)((register << 4) | (digits[(registerIndex * 4) + digitIndex] - '0'));
            }

            registers[registerIndex] = register;
        }

        return registers;
    }

    private static bool CoerceBool(object? value)
    {
        if (value is bool boolValue)
        {
            return boolValue;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    private static object? ResolveWriteScalar(PlcSignalMapping mapping, object? value)
    {
        var map = PlcValueMap.Parse(mapping.ValueMap);
        if (map.Count == 0)
        {
            return value;
        }

        var desired = value is bool boolValue ? boolValue ? "ON" : "OFF" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        foreach (var pair in map)
        {
            if (string.Equals(pair.Meaning, desired, StringComparison.OrdinalIgnoreCase))
            {
                return pair.PlcValue;
            }

            if (string.Equals(pair.PlcValue, desired, StringComparison.OrdinalIgnoreCase))
            {
                return pair.PlcValue;
            }
        }

        return value;
    }

    private static DateTimeOffset BuildTimestamp(IReadOnlyDictionary<string, PlcSignalValue> signals)
    {
        var dateText = ReadSignalText(signals, "Date");
        var timeText = ReadSignalText(signals, "Time");
        if (string.IsNullOrWhiteSpace(dateText) || string.IsNullOrWhiteSpace(timeText))
        {
            throw new PlcSignalMappingException("PLC Date and Time mappings must decode before production records can be stored.");
        }

        var date = ParseDate(dateText);
        var time = ParseTime(timeText);
        return new DateTimeOffset(date.ToDateTime(time));
    }

    private static DateOnly ParseDate(string value)
    {
        var text = DigitsOnly(value);
        if (DateOnly.TryParseExact(text, ["yyyyMMdd", "yyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return parsed;
        }

        throw new PlcSignalMappingException($"PLC Date value '{value}' could not be decoded.");
    }

    private static TimeOnly ParseTime(string value)
    {
        var text = DigitsOnly(value).PadLeft(6, '0');
        if (TimeOnly.TryParseExact(text, ["HHmmss", "HHmm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        if (TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return parsed;
        }

        throw new PlcSignalMappingException($"PLC Time value '{value}' could not be decoded.");
    }

    private static ResolvedEnumSignal ResolveEnumSignal(
        IReadOnlyDictionary<string, PlcSignalValue> signals,
        string signalName,
        string unknownLabel)
    {
        var decoded = ReadDecodedSignal(signals, signalName);
        var rawText = SignalText(decoded.RawValue);
        var resolvedText = decoded.ValueMapMatched
            ? SignalText(decoded.InterpretedValue)
            : $"Unknown {unknownLabel} ({rawText})";

        return new ResolvedEnumSignal(rawText, string.IsNullOrWhiteSpace(resolvedText)
            ? $"Unknown {unknownLabel} ({rawText})"
            : resolvedText);
    }

    private static bool IsAutoMode(string value)
    {
        return value.Equals("Auto", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("AUTO", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRunningStatus(string value)
    {
        return value.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNoError(string value)
    {
        return value.Equals("No Error", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadSignalText(IReadOnlyDictionary<string, PlcSignalValue> signals, string signalName)
    {
        return SignalText(ReadDecodedSignal(signals, signalName).InterpretedValue);
    }

    private static PlcSignalValue ReadDecodedSignal(IReadOnlyDictionary<string, PlcSignalValue> signals, string signalName)
    {
        if (!signals.TryGetValue(signalName, out var value) || value is null)
        {
            return new PlcSignalValue(null, null, false);
        }

        return value;
    }

    private static string SignalText(object? value)
    {
        return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
    }

    private static int? ReadSignalInt(IReadOnlyDictionary<string, PlcSignalValue> signals, string signalName)
    {
        var text = ReadSignalText(signals, signalName);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static decimal? ReadSignalDecimal(IReadOnlyDictionary<string, PlcSignalValue> signals, string signalName)
    {
        var text = ReadSignalText(signals, signalName);
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static bool IsEmptyOrZero(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "0.0", StringComparison.OrdinalIgnoreCase);
    }

    private static string DigitsOnly(string value)
    {
        return new string(value.Where(char.IsDigit).ToArray());
    }

    private static int RegisterCount(PlcSignalMapping mapping)
    {
        return PlcSignalMapping.RegisterCount(mapping);
    }

    private static bool CanRead(PlcSignalMapping mapping)
    {
        return mapping.Direction.Equals("Read", StringComparison.OrdinalIgnoreCase) ||
            mapping.Direction.Equals("ReadWrite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanWrite(PlcSignalMapping mapping)
    {
        return mapping.Direction.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
            mapping.Direction.Equals("ReadWrite", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseDeviceAddress(string value, char deviceCode)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 0 && char.ToUpperInvariant(trimmed[0]) == deviceCode)
        {
            trimmed = trimmed[1..];
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new PlcSignalMappingException($"PLC address '{value}' is not a valid {deviceCode} device address.");
    }

    private static int ParseModbusReference(string value, int oneBasedReferenceStart)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new PlcSignalMappingException($"PLC address '{value}' is not a valid Modbus address.");
        }

        return parsed >= oneBasedReferenceStart ? parsed - oneBasedReferenceStart : parsed;
    }

    private static int CheckedAddress(int address, PlcSignalMapping mapping)
    {
        if (address is < 0 or > 65535)
        {
            throw new PlcSignalMappingException($"PLC mapping '{mapping.SignalName}' resolved to Modbus address {address}, outside 0..65535.");
        }

        return address;
    }

    private static ushort CheckedUInt16(int value, string name)
    {
        if (value is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must fit in UInt16.");
        }

        return (ushort)value;
    }

    private static byte CheckedByte(int value, string name)
    {
        if (value is < 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must fit in byte.");
        }

        return (byte)value;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record ResolvedEnumSignal(string RawText, string ResolvedText);

    private sealed record SignalReadPlan(PlcSignalMapping Mapping, byte FunctionCode, int StartAddress, int RegisterCount, bool IsRegister);

    private sealed class RegisterBlock
    {
        public RegisterBlock(byte functionCode, int startAddress, int registerCount)
        {
            FunctionCode = functionCode;
            StartAddress = startAddress;
            RegisterCount = registerCount;
        }

        public byte FunctionCode { get; }
        public int StartAddress { get; }
        public int RegisterCount { get; set; }
        public List<SignalReadPlan> Plans { get; } = [];
    }
}

public sealed class MitsubishiPlcClient : MitsubishiModbusPlcClient
{
}
