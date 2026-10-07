namespace DynaK.Service.Models;

public sealed record ProductionRecord(
    long Id,
    string StationId,
    long PlcSequenceId,
    string SerialNumber,
    string ModelNumber,
    DateOnly Date,
    TimeOnly Time,
    DateTimeOffset Timestamp,
    string Shift,
    int TargetPartsPerShift,
    int ActualPartCount,
    int OkCount,
    int NgCount,
    int ReworkCount,
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
    string PlcSnapshotJson,
    DateTimeOffset CreatedTimestamp)
{
    public string Result => ResolvedResult;
}
