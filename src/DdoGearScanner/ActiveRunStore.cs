using System.IO;
using System.Text.Json;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// Persists the single IN-PROGRESS run to %APPDATA%\DdoCompanion\active-run.json so a crash/restart
/// mid-quest doesn't lose it — the run tracker restores it on startup (card + timer resume from the real
/// enteredUtc). Separate from runs.json, which only holds FINALIZED runs. The file is written whenever the
/// live run changes and cleared the moment it finalizes/cancels; a stale file (older than the restore
/// window) is dropped rather than resurrecting a phantom run.
/// </summary>
public static class ActiveRunStore
{
    private static string Path_ => Path.Combine(AppSettings.AppDataDir, "active-run.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>Save the live run, or clear the file when there's nothing in-progress (null or completed —
    /// a completed run already lives in runs.json). Wired to the pipeline's CurrentChanged.</summary>
    public static void Save(RunRecord? run)
    {
        try
        {
            if (run is null || run.Completed) { Clear(); return; }
            File.WriteAllText(Path_, JsonSerializer.Serialize(run, Opts));
        }
        catch { /* persistence is best-effort — never disrupt tracking */ }
    }

    public static void Clear()
    {
        try { if (File.Exists(Path_)) File.Delete(Path_); } catch { }
    }

    /// <summary>The saved in-progress run to restore on startup, or null. Drops (and deletes) a run older
    /// than <paramref name="maxAge"/> so a day-old phantom doesn't come back with a giant timer.</summary>
    public static RunRecord? Load(TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(Path_)) return null;
            RunRecord? run = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(Path_), Opts);
            if (run is null || run.Completed || DateTime.UtcNow - run.EnteredUtc > maxAge) { Clear(); return null; }
            return run;
        }
        catch { return null; }
    }
}
