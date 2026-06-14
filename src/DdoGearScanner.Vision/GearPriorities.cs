using System.Reflection;
using System.Text.Json;

namespace DdoGearScanner.Vision;

/// <summary>One stat's priority entry from Strimtom's gear evaluation (see Data/gear-priorities.json).
/// <see cref="Ranks"/> is keyed by playstyle ("melee"/"ranged"/"caster") → "A"/"B"/"C".</summary>
public sealed class PriorityEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public Dictionary<string, string> Ranks { get; init; } = new();
    public string Scales { get; init; } = "";          // "yes" / "no" / "partial"
    public string Why { get; init; } = "";
    public List<string> Aliases { get; init; } = new();

    /// <summary>Best (highest) rank across all playstyles — 'A' &lt; 'B' &lt; 'C'; null if unranked.</summary>
    public char? BestRank
    {
        get
        {
            char? best = null;
            foreach (string v in Ranks.Values)
                if (v.Length > 0 && (best is null || v[0] < best)) best = v[0];
            return best;
        }
    }

    public string? Rank(string playstyle) => Ranks.TryGetValue(playstyle, out string? r) ? r : null;
}

/// <summary>
/// Strimtom's gear-stat priority list (A/B/C per playstyle), loaded from the embedded
/// Data/gear-priorities.json. Maps a captured item-stat name to its priority entry via the entries'
/// aliases (longest alias wins, so "Fire Resistance" beats the bare "Resistance" all-saves entry).
/// Used to sort/badge the stacking matrix and (later) to drive gear recommendations.
/// </summary>
public static class GearPriorities
{
    private static readonly List<PriorityEntry> _entries = Load();
    private static readonly List<(string Alias, PriorityEntry Entry)> _byAlias =
        _entries.SelectMany(e => e.Aliases.Select(a => (a.ToLowerInvariant(), e)))
                .OrderByDescending(x => x.Item1.Length)
                .ToList();

    public static IReadOnlyList<PriorityEntry> All => _entries;

    /// <summary>The priority entry matching a captured stat name, or null if none.</summary>
    public static PriorityEntry? Lookup(string stat)
    {
        if (string.IsNullOrWhiteSpace(stat)) return null;
        string s = stat.ToLowerInvariant();
        foreach ((string alias, PriorityEntry entry) in _byAlias)
            if (s == alias) return entry;
        foreach ((string alias, PriorityEntry entry) in _byAlias)
            if (s.Contains(alias)) return entry;
        return null;
    }

    /// <summary>Best rank ('A'/'B'/'C') for a captured stat across playstyles, or null if unranked.</summary>
    public static char? RankOf(string stat) => Lookup(stat)?.BestRank;

    private static List<PriorityEntry> Load()
    {
        var list = new List<PriorityEntry>();
        try
        {
            Assembly asm = typeof(GearPriorities).Assembly;
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("gear-priorities.json", StringComparison.OrdinalIgnoreCase));
            if (name is null) return list;
            using Stream? s = asm.GetManifestResourceStream(name);
            if (s is null) return list;

            using var doc = JsonDocument.Parse(s);
            foreach (JsonElement e in doc.RootElement.GetProperty("stats").EnumerateArray())
            {
                var ranks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (e.TryGetProperty("ranks", out JsonElement rk))
                    foreach (JsonProperty p in rk.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String)
                            ranks[p.Name] = p.Value.GetString() ?? "";

                var aliases = new List<string>();
                if (e.TryGetProperty("aliases", out JsonElement al))
                    foreach (JsonElement a in al.EnumerateArray())
                        if (a.GetString() is { } str) aliases.Add(str);

                list.Add(new PriorityEntry
                {
                    Id = Str(e, "id"),
                    Name = Str(e, "name"),
                    Ranks = ranks,
                    Scales = ScalesOf(e),
                    Why = Str(e, "why"),
                    Aliases = aliases,
                });
            }
        }
        catch { /* never break the app over the data file */ }
        return list;
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string ScalesOf(JsonElement e)
    {
        if (!e.TryGetProperty("scales", out JsonElement v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            JsonValueKind.String => v.GetString() ?? "",
            _ => "",
        };
    }
}
