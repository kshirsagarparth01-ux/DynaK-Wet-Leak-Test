using System.Globalization;
using DynaK.Service.Configuration;

namespace DynaK.Service.Plc;

public sealed record ResolvedPlcResult(string? RawText, string ResolvedText, bool HasSignal);

public static class PlcResultResolver
{
    public static ResolvedPlcResult Resolve(IReadOnlyDictionary<string, PlcSignalValue> signals)
    {
        var hasOk = signals.TryGetValue(PlcSignalMapping.OkResultSignalName, out var ok);
        var hasNg = signals.TryGetValue(PlcSignalMapping.NgResultSignalName, out var ng);
        var okActive = Matches(ok, PlcSignalMapping.OkResultSignalName);
        var ngActive = Matches(ng, PlcSignalMapping.NgResultSignalName);

        if (okActive && !ngActive)
        {
            return new ResolvedPlcResult(SignalText(ok!.RawValue), PlcSignalMapping.OkResultSignalName, true);
        }
        if (ngActive && !okActive)
        {
            return new ResolvedPlcResult(SignalText(ng!.RawValue), PlcSignalMapping.NgResultSignalName, true);
        }

        var rawText = RawStatusText(hasOk ? ok : null, hasNg ? ng : null);
        var resolvedText = okActive && ngActive
            ? "Unknown Result (OK and NG are both active)"
            : rawText is null ? "Unknown Result" : $"Unknown Result ({rawText})";
        return new ResolvedPlcResult(rawText, resolvedText, hasOk || hasNg);
    }

    private static bool Matches(PlcSignalValue? signal, string expected) =>
        signal is { Error: null, ValueMapMatched: true } &&
        SignalText(signal.InterpretedValue).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static string? RawStatusText(PlcSignalValue? ok, PlcSignalValue? ng)
    {
        var values = new List<string>(2);
        if (ok is not null)
        {
            values.Add($"OK={SignalText(ok.RawValue)}");
        }
        if (ng is not null)
        {
            values.Add($"NG={SignalText(ng.RawValue)}");
        }
        return values.Count == 0 ? null : string.Join(";", values);
    }

    private static string SignalText(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
}
