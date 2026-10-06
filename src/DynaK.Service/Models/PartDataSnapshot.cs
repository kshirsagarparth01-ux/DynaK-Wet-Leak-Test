using DynaK.Service.Plc;

namespace DynaK.Service.Models;

public sealed record PartDataSnapshot(
    string StationId,
    string? SerialNumber,
    string QrCode,
    string PartNumber,
    int? TargetPartsPerShift,
    int? ActualPartCount,
    decimal? LeakTestValue,
    string LeakTestUnit,
    decimal LowerLimit,
    decimal UpperLimit,
    string? RawResultValue,
    string ResolvedResult,
    string? RawModeValue,
    string ResolvedMode,
    bool IsAutoMode,
    bool IsMachineRunning,
    string? ErrorCode,
    string? ErrorDescription,
    string? RawRunningStatusValue,
    string? ResolvedRunningStatus,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, PlcSignalValue> Signals)
{
    public string Result => ResolvedResult;
}
