namespace DdoGearScanner.Model;

/// <summary>
/// One dungeon/quest run: which quest, at what difficulty, the character + level that ran it, when it
/// started/ended, and the XP awarded at completion. Produced automatically by the run tracker (OCR of
/// DDO's quest tracker + end-of-quest reward panel) and hand-correctable afterward — the same
/// best-effort-then-edit model as <see cref="GearItem"/>, so imperfect OCR degrades gracefully rather
/// than losing the run. <see cref="RawOcrText"/> is retained so nothing is lost when parsing misses.
/// Persisted to %APPDATA%\DdoGearScanner\runs.json (see RunStore); <see cref="CharacterId"/> ties each
/// run to a scanned character.
/// </summary>
public sealed record RunRecord(
    string Id,
    string DungeonName,
    // Casual/Normal/Hard/Elite/Reaper/Solo, or null when it couldn't be read (e.g. an abandoned run
    // never showed the reward panel). User-editable.
    string? Difficulty,
    int? CharacterLevel,
    string? CharacterId,
    DateTime EnteredUtc,
    DateTime? CompletedUtc,
    int? Xp,
    // true = the run finished (a completion/reward panel was seen); false = left/abandoned.
    bool Completed,
    string RawOcrText,
    // Marks a run whose fields were hand-corrected in the run tracker window.
    bool Edited = false,
    // The quest's level from the adventure-entry popup (reliably OCR'd, unlike the character's own level
    // which is optional/manual). Distinct from CharacterLevel.
    int? QuestLevel = null,
    // Character NAME auto-detected from the avatar region (name above the health bar), when calibrated.
    // Distinct from CharacterId (the scanned-gear profile); best-effort OCR, user-editable.
    string? CharacterName = null)
{
    /// <summary>Wall-clock length of the run, or null if it never ended (or the clock looks bogus).</summary>
    public TimeSpan? Duration => CompletedUtc is { } c && c > EnteredUtc ? c - EnteredUtc : null;

    /// <summary>XP earned per minute — the number that actually matters for XP farming — or null when
    /// XP or a sane duration is missing.</summary>
    public double? XpPerMinute => (Xp, Duration) is ({ } xp, { } d) && d.TotalMinutes > 0
        ? xp / d.TotalMinutes
        : null;

    /// <summary>Short opaque id for identifying a run across edits/sorting in the UI.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}
