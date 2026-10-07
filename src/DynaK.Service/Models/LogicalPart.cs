namespace DynaK.Service.Models;

public sealed record LogicalPart(
    long Id,
    string StationId,
    //code change by chatgpt
    // string QrCode,
    string PartNumber,
    string OverallResult,
    DateTimeOffset CreatedTimestamp,
    DateTimeOffset UpdatedTimestamp,
    ProductionRecord LatestAttempt,
    IReadOnlyList<ProductionRecord> Attempts);
