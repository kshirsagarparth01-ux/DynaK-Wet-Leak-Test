namespace DynaK.Service.Configuration;

public sealed class ShiftResolver
{
    private readonly IReadOnlyList<ShiftDefinition> _shifts;

    public ShiftResolver(IReadOnlyList<ShiftDefinition> shifts)
    {
        _shifts = shifts.Count == 0 ? AppSettings.CreateDefaultShifts() : shifts;
    }

    public ShiftDefinition Resolve(DateTimeOffset timestamp)
    {
        var time = TimeOnly.FromDateTime(timestamp.DateTime);
        foreach (var shift in _shifts)
        {
            if (Contains(shift, time))
            {
                return shift;
            }
        }

        return _shifts[0];
    }

    public ShiftWindow ResolveWindow(DateTimeOffset timestamp)
    {
        var shift = Resolve(timestamp);
        var local = timestamp.DateTime;
        var startsOn = local.Date;
        if (shift.StartsAt > shift.EndsAt && TimeOnly.FromDateTime(local) < shift.EndsAt)
        {
            startsOn = startsOn.AddDays(-1);
        }

        var endsOn = shift.StartsAt < shift.EndsAt ? startsOn : startsOn.AddDays(1);
        var start = new DateTimeOffset(startsOn.Add(shift.StartsAt.ToTimeSpan()), timestamp.Offset);
        var end = new DateTimeOffset(endsOn.Add(shift.EndsAt.ToTimeSpan()), timestamp.Offset);
        return new ShiftWindow(shift, start, end);
    }

    private static bool Contains(ShiftDefinition shift, TimeOnly time)
    {
        if (shift.StartsAt == shift.EndsAt)
        {
            return true;
        }

        if (shift.StartsAt < shift.EndsAt)
        {
            return time >= shift.StartsAt && time < shift.EndsAt;
        }

        return time >= shift.StartsAt || time < shift.EndsAt;
    }
}

public sealed record ShiftWindow(ShiftDefinition Shift, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
