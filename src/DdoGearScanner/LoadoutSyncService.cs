namespace DdoGearScanner;

/// <summary>
/// Pushes EVERY character's equipped loadout to the DDO Gear Planner account — on startup, whenever gear
/// changes (debounced), and on a periodic tick. The scanner is the source of truth for equipped gear; the web
/// planner reads it as a starting point — so this is push-only (no pull). Reads each character's saved
/// loadout file (so all characters sync, not just the active one). Best-effort; a failed push waits for the
/// next trigger. Mirrors RunSyncService/CharacterSyncService.
/// </summary>
public sealed class LoadoutSyncService : IDisposable
{
    private readonly CaptureStore _store;
    private readonly CharacterStore _chars;
    private readonly RunSyncClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Timers.Timer _periodic;
    private volatile bool _again;

    public LoadoutSyncService(CaptureStore store, CharacterStore chars, RunSyncClient client)
    {
        _store = store;
        _chars = chars;
        _client = client;
        _store.Changed += OnChanged;
        _periodic = new System.Timers.Timer(TimeSpan.FromMinutes(5).TotalMilliseconds) { AutoReset = true };
        _periodic.Elapsed += (_, _) => Trigger();
        _periodic.Start();
    }

    /// <summary>Push all loadouts — call on startup and after the API key changes.</summary>
    public void Start()
    {
        if (_client.IsConfigured) Trigger();
    }

    private void OnChanged() => Trigger();

    private void Trigger() => _ = PushAsync();

    private async Task PushAsync()
    {
        if (!_client.IsConfigured) return;
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) { _again = true; return; }
        try
        {
            do
            {
                _again = false;
                foreach (Model.CharacterProfile p in _chars.Profiles)
                {
                    // Prefer the in-memory loadout for the active character (always current); read the file for
                    // the rest. Both are keyed by slug (p.Id).
                    var loadout = p.Id == _store.CharacterId ? _store.Loadout : CaptureStore.ReadLoadout(p.Id);
                    if (loadout.Count == 0) continue;
                    await _client.PushLoadoutAsync(p.Id, loadout).ConfigureAwait(false);
                }
            } while (_again);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _store.Changed -= OnChanged;
        _periodic.Dispose();
        _gate.Dispose();
    }
}
