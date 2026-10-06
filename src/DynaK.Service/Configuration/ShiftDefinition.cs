namespace DynaK.Service.Configuration;

public sealed record ShiftDefinition(
    string Name,
    TimeOnly StartsAt,
    TimeOnly EndsAt);
