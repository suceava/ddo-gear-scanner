using System.IO;
using System.Text.Json;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// The append-only log of completed/abandoned dungeon runs, persisted to
/// %APPDATA%\DdoGearScanner\runs.json. Each <see cref="RunRecord"/> carries its own CharacterId, so all
/// characters share one file and the UI filters by character. Mirrors <see cref="CaptureStore"/>'s
/// swallow-on-error persistence (losing one save beats crashing).
/// </summary>
public sealed class RunStore
{
    private static readonly string Dir = AppSettings.AppDataDir;
    private static readonly string StorePath = Path.Combine(Dir, "runs.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly List<RunRecord> _runs = new();
    public IReadOnlyList<RunRecord> Runs => _runs;

    public static RunStore Load()
    {
        var store = new RunStore();
        try
        {
            if (File.Exists(StorePath))
            {
                var loaded = JsonSerializer.Deserialize<List<RunRecord>>(File.ReadAllText(StorePath), JsonOpts);
                if (loaded is not null) store._runs.AddRange(loaded);
            }
        }
        catch { /* start empty rather than crash on a corrupt file */ }
        return store;
    }

    /// <summary>All runs, newest first (the run tracker shows every run — character is per-run now).</summary>
    public IEnumerable<RunRecord> AllNewestFirst()
        => _runs.OrderByDescending(r => r.EnteredUtc);

    public void Add(RunRecord run)
    {
        _runs.Add(run);
        Save();
    }

    /// <summary>Replace a run (by Id) after a hand-edit; no-op if the id is gone.</summary>
    public void Update(RunRecord updated)
    {
        int i = _runs.FindIndex(r => r.Id == updated.Id);
        if (i < 0) return;
        _runs[i] = updated;
        Save();
    }

    public void Remove(string id)
    {
        if (_runs.RemoveAll(r => r.Id == id) > 0) Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_runs, JsonOpts));
        }
        catch { /* losing one save beats crashing */ }
    }
}
