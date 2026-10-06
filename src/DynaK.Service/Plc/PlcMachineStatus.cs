namespace DynaK.Service.Plc;

public sealed record PlcMachineStatus(
    bool IsConnected,
    bool IsMachineRunning,
    string? ErrorCode,
    string? ErrorDescription,
    DateTimeOffset Timestamp);
