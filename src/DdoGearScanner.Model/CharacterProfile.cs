namespace DdoGearScanner.Model;

/// <summary>A character's combat style — drives the gear-stat priority ranking (Strimtom A/B/C
/// differs per playstyle). <see cref="Unknown"/> falls back to the best rank across all styles.</summary>
public enum Playstyle
{
    Unknown,
    Melee,
    Ranged,
    Caster,
}

/// <summary>
/// One scanned character: identity + combat style (so the matrix/recommendations rank for the right
/// playstyle) plus optional classes/level. Gear is stored per character in loadout-&lt;Id&gt;.json.
/// </summary>
public sealed record CharacterProfile(
    string Id,
    string Name,
    Playstyle Playstyle,
    string? Classes = null,
    int? Level = null)
{
    /// <summary>Dropdown label, e.g. "Throgar — Melee".</summary>
    public string Display => Playstyle == Playstyle.Unknown ? Name : $"{Name} — {Playstyle}";

    /// <summary>The playstyle key used by the gear-priority data ("melee"/"ranged"/"caster").</summary>
    public string? PlaystyleKey => Playstyle switch
    {
        Playstyle.Melee => "melee",
        Playstyle.Ranged => "ranged",
        Playstyle.Caster => "caster",
        _ => null,
    };
}
