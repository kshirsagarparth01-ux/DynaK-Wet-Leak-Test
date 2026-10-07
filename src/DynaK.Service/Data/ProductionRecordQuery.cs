using DynaK.Service.Models;

namespace DynaK.Service.Data;

public sealed record ProductionRecordQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Shift,
    string? SerialNumber,
    string? ModelNumber,
    ProductionResult? Result,
    int Limit = 200);
