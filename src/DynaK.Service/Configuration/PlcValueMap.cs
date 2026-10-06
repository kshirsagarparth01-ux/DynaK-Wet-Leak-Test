using System.Globalization;

namespace DynaK.Service.Configuration;

public sealed record PlcValueMapEntry(string PlcValue, string Meaning);

public static class PlcValueMap
{
    public static IReadOnlyList<PlcValueMapEntry> Parse(string? valueMap)
    {
        var entries = new List<PlcValueMapEntry>();
        if (string.IsNullOrWhiteSpace(valueMap))
        {
            return entries;
        }

        foreach (var entry in valueMap.Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                entries.Add(new PlcValueMapEntry(entry.Trim(), ""));
                continue;
            }

            entries.Add(new PlcValueMapEntry(entry[..separator].Trim(), entry[(separator + 1)..].Trim()));
        }

        return entries;
    }

    public static bool TryResolve(string? valueMap, object? rawValue, out string meaning)
    {
        var map = Parse(valueMap)
            .Where(entry => entry.Meaning.Length > 0)
            .ToDictionary(entry => entry.PlcValue, entry => entry.Meaning, StringComparer.OrdinalIgnoreCase);

        foreach (var key in CandidateKeys(rawValue))
        {
            if (map.TryGetValue(key, out meaning!))
            {
                return true;
            }
        }

        meaning = "";
        return false;
    }

    public static IEnumerable<string> CandidateKeys(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        yield return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (value is decimal decimalValue)
        {
            yield return decimalValue.ToString("0", CultureInfo.InvariantCulture);
        }
        else if (value is bool boolValue)
        {
            yield return boolValue ? "1" : "0";
            yield return boolValue ? "ON" : "OFF";
            yield return boolValue ? "TRUE" : "FALSE";
        }
    }
}
