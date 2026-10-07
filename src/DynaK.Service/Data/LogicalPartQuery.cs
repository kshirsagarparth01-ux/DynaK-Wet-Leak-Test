namespace DynaK.Service.Data;

public sealed record LogicalPartQuery(
    DateTimeOffset? From,
    DateTimeOffset? ToExclusive,
    string? Shift,
    string? PartNumber,
    //code change by chatgpt
    // string? QrCode,
    string? Result,
    int? Limit = 200);
