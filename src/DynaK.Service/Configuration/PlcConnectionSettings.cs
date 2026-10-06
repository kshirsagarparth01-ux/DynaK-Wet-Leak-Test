namespace DynaK.Service.Configuration;

public sealed class PlcConnectionSettings
{
    public string? IpAddress { get; set; } = "192.168.255.1";
    public int? Port { get; set; } = 502;
    public int? UnitId { get; set; } = 1;
    public int DRegisterModbusOffset { get; set; }
    public int PollingIntervalMs { get; set; } = 1000;
    public int ReconnectIntervalMs { get; set; } = 5000;
    public int ConnectionTimeoutMs { get; set; } = 3000;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(IpAddress) && Port is not null;

    public PlcConnectionSettings Clone() => new()
    {
            IpAddress = IpAddress,
            Port = Port,
            UnitId = UnitId,
            DRegisterModbusOffset = DRegisterModbusOffset,
            PollingIntervalMs = PollingIntervalMs,
            ReconnectIntervalMs = ReconnectIntervalMs,
            ConnectionTimeoutMs = ConnectionTimeoutMs
    };
}
