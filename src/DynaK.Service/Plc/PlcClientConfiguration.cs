using System.Globalization;
using System.Text;
using DynaK.Service.Configuration;

namespace DynaK.Service.Plc;

public sealed record PlcClientConfiguration(
    string StationId,
    string LeakTestUnit,
    decimal LowerLimit,
    decimal UpperLimit,
    PlcConnectionSettings Connection,
    IReadOnlyList<PlcSignalMapping> SignalMappings)
{
    public bool IsConfigured => Connection.IsConfigured;

    public static PlcClientConfiguration From(AppSettings settings) =>
        new(
            settings.StationId,
            settings.LeakTestUnit,
            settings.LowerLimit,
            settings.UpperLimit,
            settings.Plc.Clone(),
            settings.SignalMappings.Select(mapping => mapping.Clone()).ToList());

    public string Fingerprint()
    {
        var builder = new StringBuilder();
        builder.Append(StationId).Append('|')
            .Append(LeakTestUnit).Append('|')
            .Append(LowerLimit.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(UpperLimit.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(Connection.IpAddress).Append('|')
            .Append(Connection.Port).Append('|')
            .Append(Connection.UnitId).Append('|')
            .Append(Connection.DRegisterModbusOffset).Append('|')
            .Append(Connection.PollingIntervalMs).Append('|')
            .Append(Connection.ReconnectIntervalMs).Append('|')
            .Append(Connection.ConnectionTimeoutMs);

        foreach (var mapping in SignalMappings.OrderBy(mapping => mapping.SignalName, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('|')
                .Append(mapping.SignalName).Append('=')
                .Append(mapping.Enabled).Append(',')
                .Append(mapping.Address).Append(',')
                .Append(mapping.AddressType).Append(',')
                .Append(mapping.DataType).Append(',')
                .Append(mapping.Direction).Append(',')
                .Append(mapping.Length).Append(',')
                .Append(mapping.ScalingFactor.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(mapping.Offset.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(mapping.ByteOrder).Append(',')
                .Append(mapping.WordOrder).Append(',')
                .Append(mapping.Encoding).Append(',')
                .Append(mapping.Format).Append(',')
                .Append(mapping.ValueMap);
        }

        return builder.ToString();
    }
}
