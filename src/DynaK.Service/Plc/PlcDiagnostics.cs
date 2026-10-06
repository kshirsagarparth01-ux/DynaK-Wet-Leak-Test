using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DynaK.Service.Configuration;

namespace DynaK.Service.Plc;

public static class PlcNetworkDiagnostics
{
    public static IReadOnlyList<PlcNetworkInterfaceDiagnostic> Capture(string? plcIpAddress)
    {
        try
        {
            var hasPlcAddress = IPAddress.TryParse(plcIpAddress, out var plcAddress) &&
                plcAddress.AddressFamily == AddressFamily.InterNetwork;
            var interfaces = new List<PlcNetworkInterfaceDiagnostic>();

            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                             item.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                             item.NetworkInterfaceType != NetworkInterfaceType.Tunnel))
            {
                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses
                             .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    var mask = unicast.IPv4Mask;
                    interfaces.Add(new PlcNetworkInterfaceDiagnostic(
                        networkInterface.Name,
                        networkInterface.NetworkInterfaceType.ToString(),
                        unicast.Address.ToString(),
                        mask?.ToString(),
                        hasPlcAddress && mask is not null && IsSameSubnet(unicast.Address, plcAddress!, mask)));
                }
            }

            return interfaces;
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private static bool IsSameSubnet(IPAddress local, IPAddress remote, IPAddress mask)
    {
        var localBytes = local.GetAddressBytes();
        var remoteBytes = remote.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        return localBytes.Length == remoteBytes.Length &&
            localBytes.Length == maskBytes.Length &&
            localBytes.Select((value, index) => (byte)(value & maskBytes[index]))
                .SequenceEqual(remoteBytes.Select((value, index) => (byte)(value & maskBytes[index])));
    }
}

public static class PlcErrorClassifier
{
    public static string Classify(Exception exception)
    {
        var root = exception.GetBaseException();
        if (root is PlcSignalMappingException)
        {
            return "INVALID_ADDRESS_OR_MAPPING";
        }

        if (root is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.NetworkDown or SocketError.NetworkUnreachable => "NETWORK_UNAVAILABLE",
                SocketError.HostDown or SocketError.HostUnreachable or SocketError.HostNotFound => "HOST_UNREACHABLE",
                SocketError.ConnectionRefused => "CONNECTION_REFUSED",
                SocketError.TimedOut => "TCP_CONNECTION_TIMEOUT",
                _ => $"SOCKET_{socket.SocketErrorCode.ToString().ToUpperInvariant()}"
            };
        }

        if (root is OperationCanceledException)
        {
            return "TCP_CONNECTION_TIMEOUT";
        }

        if (root is IOException io &&
            (io.Message.Contains("Modbus", StringComparison.OrdinalIgnoreCase) ||
             io.Message.Contains("function", StringComparison.OrdinalIgnoreCase) ||
             io.Message.Contains("response", StringComparison.OrdinalIgnoreCase)))
        {
            return "PROTOCOL_OR_REGISTER_READ_ERROR";
        }

        if (root is IOException)
        {
            return "CONNECTION_LOST";
        }

        return "COMMUNICATION_LIBRARY_ERROR";
    }
}

public sealed record PlcNetworkInterfaceDiagnostic(
    string Name,
    string Type,
    string Ipv4Address,
    string? SubnetMask,
    bool CompatibleWithPlc);

public sealed record PlcSignalDiagnostic(
    string SignalName,
    string Address,
    string DataType,
    string? RawValue,
    string? InterpretedValue,
    string? Error);

public sealed record PlcCommissioningDiagnostic(
    bool ReadOnly,
    string Protocol,
    string CommunicationLibrary,
    string? PlcIpAddress,
    int? PlcPort,
    int? UnitId,
    int DRegisterModbusOffset,
    int PollingIntervalMs,
    int ConnectionTimeoutMs,
    IReadOnlyList<PlcNetworkInterfaceDiagnostic> PcNetworkInterfaces,
    bool CompatibleSubnetFound,
    DateTimeOffset? LastSuccessfulRead,
    string? LastErrorCategory,
    string? LastError,
    IReadOnlyList<PlcSignalDiagnostic> Signals)
{
    public static PlcCommissioningDiagnostic From(AppSettings settings)
    {
        var interfaces = PlcNetworkDiagnostics.Capture(settings.Plc.IpAddress);
        var handshakeWritesDisabled = new[] { "System Ready", "Communication OK", "Data Saved" }
            .Any(signalName => !settings.SignalMappings.Any(mapping =>
                mapping.Enabled &&
                (mapping.Direction.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
                 mapping.Direction.Equals("ReadWrite", StringComparison.OrdinalIgnoreCase)) &&
                mapping.SignalName.Equals(signalName, StringComparison.OrdinalIgnoreCase)));
        return new PlcCommissioningDiagnostic(
            handshakeWritesDisabled,
            "Modbus TCP",
            ".NET TcpClient (built-in Modbus TCP client)",
            settings.Plc.IpAddress,
            settings.Plc.Port,
            settings.Plc.UnitId,
            settings.Plc.DRegisterModbusOffset,
            settings.Plc.PollingIntervalMs,
            settings.Plc.ConnectionTimeoutMs,
            interfaces,
            interfaces.Any(item => item.CompatibleWithPlc),
            null,
            null,
            null,
            BuildSignals(settings.SignalMappings, null));
    }

    public static IReadOnlyList<PlcSignalDiagnostic> BuildSignals(
        IReadOnlyList<PlcSignalMapping> mappings,
        IReadOnlyDictionary<string, PlcSignalValue>? values)
    {
        return mappings
            .Select(mapping =>
            {
                PlcSignalValue? value = null;
                values?.TryGetValue(mapping.SignalName, out value);
                return new PlcSignalDiagnostic(
                    mapping.SignalName,
                    mapping.Address ?? "--",
                    mapping.DataType,
                    InvariantText(value?.RawValue),
                    InvariantText(value?.InterpretedValue),
                    value?.Error);
            })
            .ToList();
    }

    private static string? InvariantText(object? value) =>
        value is null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
}
