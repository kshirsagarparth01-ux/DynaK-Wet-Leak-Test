using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DynaK.Service.Configuration;

namespace DynaK.Service.Plc;

public sealed record PlcDecodedValue(object? Value, string RawText);

public static class PlcValueDecoder
{
    public static PlcDecodedValue Decode(PlcSignalMapping mapping, IReadOnlyList<ushort> registers)
    {
        if (!PlcSignalMapping.TryCanonicalizeDataType(mapping.DataType, out var dataType))
        {
            throw new PlcSignalMappingException($"PLC mapping '{mapping.SignalName}' has unsupported data type '{mapping.DataType}'.");
        }

        var isNumericTimeRange = IsNumericTimeRange(mapping, dataType);
        var requiredRegisters = PlcSignalMapping.IsAsciiString(dataType) || isNumericTimeRange
            ? Math.Max(1, PlcSignalMapping.RegisterCount(mapping))
            : PlcSignalMapping.MinimumRegisterCount(dataType) ?? 1;
        if (registers.Count < requiredRegisters)
        {
            throw new PlcSignalMappingException($"PLC mapping '{mapping.SignalName}' requires at least {requiredRegisters} register(s) for {dataType}, received {registers.Count}.");
        }

        var decodedRegisters = PlcSignalMapping.IsAsciiString(dataType) || isNumericTimeRange
            ? registers
            : registers.Take(requiredRegisters).ToArray();

        object? value = dataType switch
        {
            "Int16" when isNumericTimeRange => DecodeTime(decodedRegisters, signed: true),
            "UInt16" or "Word16" when isNumericTimeRange => DecodeTime(decodedRegisters, signed: false),
            "Bool" => decodedRegisters[0] != 0,
            "Int16" => (decimal)unchecked((short)decodedRegisters[0]),
            "UInt16" or "Word16" => (decimal)decodedRegisters[0],
            "Int32" => (decimal)BinaryPrimitives.ReadInt32BigEndian(OrderedBytes(mapping, decodedRegisters).AsSpan(0, 4)),
            "UInt32" or "DWord32" => (decimal)BinaryPrimitives.ReadUInt32BigEndian(OrderedBytes(mapping, decodedRegisters).AsSpan(0, 4)),
            "Int64" => (decimal)BinaryPrimitives.ReadInt64BigEndian(OrderedBytes(mapping, decodedRegisters).AsSpan(0, 8)),
            "UInt64" or "QWord64" => (decimal)BinaryPrimitives.ReadUInt64BigEndian(OrderedBytes(mapping, decodedRegisters).AsSpan(0, 8)),
            "Float32" => (decimal)BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(OrderedBytes(mapping, decodedRegisters).AsSpan(0, 4))),
            "Float64" => (decimal)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(OrderedBytes(mapping, decodedRegisters).AsSpan(0, 8))),
            "AsciiString" => DecodeAscii(mapping, decodedRegisters),
            "Bcd16" or "Bcd32" => DecodeBcd(decodedRegisters),
            _ => throw new PlcSignalMappingException($"PLC mapping '{mapping.SignalName}' has unsupported data type '{mapping.DataType}'.")
        };

        return new PlcDecodedValue(value, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
    }

    private static bool IsNumericTimeRange(PlcSignalMapping mapping, string dataType) =>
        mapping.SignalName.Equals("Time", StringComparison.OrdinalIgnoreCase) &&
        PlcSignalMapping.RegisterCount(mapping) == 3 &&
        dataType is "Int16" or "UInt16" or "Word16";

    private static string DecodeTime(IReadOnlyList<ushort> registers, bool signed)
    {
        var hour = signed ? (int)unchecked((short)registers[0]) : registers[0];
        var minute = signed ? (int)unchecked((short)registers[1]) : registers[1];
        var second = signed ? (int)unchecked((short)registers[2]) : registers[2];
        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
        {
            throw new PlcSignalMappingException($"PLC Time registers contain an invalid value '{hour},{minute},{second}'.");
        }

        return $"{hour:00}:{minute:00}:{second:00}";
    }

    private static byte[] OrderedBytes(PlcSignalMapping mapping, IReadOnlyList<ushort> registers)
    {
        var ordered = mapping.WordOrder.Equals("LowHigh", StringComparison.OrdinalIgnoreCase) && registers.Count > 1
            ? registers.Reverse().ToArray()
            : registers.ToArray();
        var bytes = new byte[ordered.Length * 2];
        for (var index = 0; index < ordered.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(index * 2, 2), ordered[index]);
        }

        return ApplyByteOrder(mapping.ByteOrder, bytes);
    }

    private static byte[] ApplyByteOrder(string byteOrder, byte[] bytes)
    {
        var ordered = bytes.ToArray();
        switch (byteOrder.ToUpperInvariant())
        {
            case "":
            case "ABCD":
                return ordered;
            case "BADC":
                for (var index = 0; index + 1 < ordered.Length; index += 2)
                {
                    (ordered[index], ordered[index + 1]) = (ordered[index + 1], ordered[index]);
                }
                return ordered;
            case "CDAB":
                for (var index = 0; index + 3 < ordered.Length; index += 4)
                {
                    (ordered[index], ordered[index + 2]) = (ordered[index + 2], ordered[index]);
                    (ordered[index + 1], ordered[index + 3]) = (ordered[index + 3], ordered[index + 1]);
                }
                return ordered;
            case "DCBA":
                for (var index = 0; index + 3 < ordered.Length; index += 4)
                {
                    Array.Reverse(ordered, index, 4);
                }
                return ordered;
            default:
                return ordered;
        }
    }

    private static string DecodeAscii(PlcSignalMapping mapping, IReadOnlyList<ushort> registers)
    {
        var bytes = OrderedBytes(mapping, registers);
        var text = TextEncoding(mapping).GetString(bytes);
        var terminator = text.IndexOf('\0', StringComparison.Ordinal);
        if (terminator >= 0)
        {
            text = text[..terminator];
        }

        return text.TrimEnd(' ');
    }

    private static Encoding TextEncoding(PlcSignalMapping mapping) =>
        mapping.Encoding.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8 : Encoding.ASCII;

    private static decimal DecodeBcd(IReadOnlyList<ushort> registers)
    {
        var builder = new StringBuilder();
        foreach (var register in registers)
        {
            for (var shift = 12; shift >= 0; shift -= 4)
            {
                var digit = (register >> shift) & 0x0F;
                if (digit > 9)
                {
                    throw new PlcSignalMappingException("PLC BCD value contains a non-decimal nibble.");
                }

                builder.Append(digit);
            }
        }

        return decimal.Parse(builder.ToString().TrimStart('0').PadLeft(1, '0'), CultureInfo.InvariantCulture);
    }
}
