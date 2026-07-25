using System.IO;
using System.Text.Json;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// The append-only log of completed/abandoned dungeon runs, persisted to
/// %APPDATA%\DdoCompanion\runs.json. Each <see cref="RunRecord"/> carries its own CharacterId, so all
/// characters share one file and the UI filters by character. Mirrors <see cref="CaptureStore"/>'s
/// swallow-on-error persistence (losing one save beats crashing).
///
/// Accessed from multiple threads (the run-tracker pipeline adds/updates from the capture thread; the UI
/// edits/removes from the dispatcher thread; the sync service marks-synced from a background worker), so
/// all list access is locked. Mutations raise <see cref="RunSaved"/>/<see cref="RunRemoved"/> (OUTSIDE the
/// lock) which the cloud-sync outbox listens to; <see cref="MarkSynced"/> is the one mutation that does NOT
/// fire an event, so recording a successful push can't loop back into another push.
/// </summary>
public sealed class RunStore
{
    private static readonly string Dir = AppSettings.AppDataDir;
    private static readonly string StorePath = Path.Combine(Dir, "runs.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly List<RunRecord> _runs = new();

    /// <summary>A run was added or edited — an upsert for the cloud outbox. Fired outside the lock.</summary>
    public event Action<RunRecord>? RunSaved;

    /// <summary>A run was deleted (its id) — a delete for the cloud outbox. Fired outside the lock.</summary>
    public event Action<string>? RunRemoved;

    /// <summary>The set of runs changed because of a PULL from the server (a web edit/delete came down), not a
    /// local action. The UI reloads on this; it deliberately does NOT feed the outbox (no re-push loop).</summary>
    public event Action? Changed;

    public IReadOnlyList<RunRecord> Runs
    {
        get { lock (_gate) return _runs.ToList(); }
    }

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
    {
        lock (_gate) return _runs.OrderByDescending(r => r.EnteredUtc).ToList();
    }

    /// <summary>Runs not yet pushed to the cloud (the outbox to drain).</summary>
    public IReadOnlyList<RunRecord> Unsynced()
    {
        lock (_gate) return _runs.Where(r => !r.Synced).ToList();
    }

    public void Add(RunRecord run)
    {
        lock (_gate)
        {
            _runs.Add(run);
            SaveLocked();
        }
        RunSaved?.Invoke(run);
    }

    /// <summary>Replace a run (by Id) after a hand-edit; no-op if the id is gone. Editing marks it dirty
    /// (Synced=false) so it re-pushes.</summary>
    public void Update(RunRecord updated)
    {
        RunRecord dirty = updated with { Synced = false };
        bool found;
        lock (_gate)
        {
            int i = _runs.FindIndex(r => r.Id == dirty.Id);
            found = i >= 0;
            if (found)
            {
                _runs[i] = dirty;
                SaveLocked();
            }
        }
        if (found) RunSaved?.Invoke(dirty);
    }

    public void Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _runs.RemoveAll(r => r.Id == id) > 0;
            if (removed) SaveLocked();
        }
        if (removed) RunRemoved?.Invoke(id);
    }

    /// <summary>
    /// Merge the server's runs into the local cache (the pull half of two-way sync). The server is the source
    /// of truth for HISTORY; local is a cache + an outbox. Per run:
    ///  - local not-yet-pushed (Synced=false) → KEEP (it's in the outbox; it wins until it pushes).
    ///  - local synced &amp; on server → ADOPT the server's version (that's a web edit), preserving the
    ///    local-only CharacterId and the immutable EnteredUtc anchor.
    ///  - local synced &amp; ABSENT from server → a web delete → DROP it.
    ///  - server-only → ADD it (restores history on a fresh install / another PC).
    /// Safety: <paramref name="server"/> must be a REAL 200 result (the client returns null otherwise and the
    /// caller skips reconcile), and an EMPTY server list never deletes anything — that guards a bad key / first
    /// sync (lots of unsynced local, nothing server-side yet) from wiping local history. Returns true if
    /// anything changed and fires <see cref="Changed"/> (outside the lock); never fires RunSaved/RunRemoved.
    /// </summary>
    public bool ReconcileFromServer(IReadOnlyList<RunRecord> server)
    {
        bool changed;
        lock (_gate)
        {
            RunReconciler.Result result = RunReconciler.Merge(_runs, server);
            changed = result.Changed;
            if (changed) { _runs.Clear(); _runs.AddRange(result.Runs); SaveLocked(); }
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>Record that a run was successfully pushed. Does NOT fire RunSaved (no re-push loop).</summary>
    public void MarkSynced(string id)
    {
        lock (_gate)
        {
            int i = _runs.FindIndex(r => r.Id == id);
            if (i < 0 || _runs[i].Synced) return;
            _runs[i] = _runs[i] with { Synced = true };
            SaveLocked();
        }
    }

    public void Save()
    {
        lock (_gate) SaveLocked();
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_runs, JsonOpts));
        }
        catch { /* losing one save beats crashing */ }
    }
}
