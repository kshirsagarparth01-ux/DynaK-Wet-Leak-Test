namespace DynaK.Service.Data;

public sealed record LogicalPartQuery(
    DateTimeOffset? From,
    DateTimeOffset? ToExclusive,
    string? Shift,
    string? SerialNumber,
    string? ModelNumber,
    string? Result,
    int? Limit = 200);
