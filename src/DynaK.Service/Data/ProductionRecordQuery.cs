using DynaK.Service.Models;

namespace DynaK.Service.Data;

public sealed record ProductionRecordQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Shift,
    string? PartNumber,
    //code change by chatgpt
    // string? QrCode,
    ProductionResult? Result,
    int Limit = 200);
