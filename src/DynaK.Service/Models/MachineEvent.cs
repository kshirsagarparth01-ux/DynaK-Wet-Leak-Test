namespace DynaK.Service.Models;

public sealed record MachineEvent(
    long Id,
    string StationId,
    string EventType,
    string Severity,
    string? Code,
    string Description,
    DateTimeOffset StartedTimestamp,
    DateTimeOffset? ClearedTimestamp,
    DateTimeOffset CreatedTimestamp);
