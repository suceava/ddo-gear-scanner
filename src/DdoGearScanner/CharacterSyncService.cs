namespace DdoGearScanner;

/// <summary>
/// Two-way cloud sync for the character LIST (keyed by slug(name), the shared identity). On any local change
/// (and on startup / key change / a periodic tick) it pushes every local character (idempotent upsert) then
/// pulls the account's characters and merges in any it doesn't have — so the scanner shows the same characters
/// as the web app (including run- and web-created ones). The character set is tiny, so a push-all + pull each
/// cycle is fine; no per-op outbox. Playstyle/classes/loadout stay local (not synced). Best-effort: any
/// failure just leaves things to the next tick. Gear/holdings sync is a separate, later concern.
/// </summary>
public sealed class CharacterSyncService : IDisposable
{
    private readonly CharacterStore _store;
    private readonly RunSyncClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Timers.Timer _periodic;
    private volatile bool _again;

    public CharacterSyncService(CharacterStore store, RunSyncClient client)
    {
        _store = store;
        _client = client;
        _store.Changed += OnChanged;
        _periodic = new System.Timers.Timer(TimeSpan.FromMinutes(5).TotalMilliseconds) { AutoReset = true };
        _periodic.Elapsed += (_, _) => Trigger();
        _periodic.Start();
    }

    /// <summary>Kick a sync — call on startup and after the API key changes.</summary>
    public void Start()
    {
        if (_client.IsConfigured) Trigger();
    }

    private void OnChanged() => Trigger();

    private void Trigger() => _ = SyncAsync();

    private async Task SyncAsync()
    {
        if (!_client.IsConfigured) return;
        // Coalesce bursts: one cycle at a time; a trigger mid-cycle loops once more.
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) { _again = true; return; }
        try
        {
            do
            {
                _again = false;
                // Don't push the auto-created "Character N" placeholder — it'd pollute the account (and the
                // web). Real characters (renamed, or created from runs) still sync; pull brings everything down.
                var local = _store.Profiles.Where(p => !IsPlaceholder(p.Name)).Select(p => (p.Id, p.Name)).ToList();
                await _client.PushCharactersAsync(local).ConfigureAwait(false);
                IReadOnlyList<ServerCharacter>? server = await _client.PullCharactersAsync().ConfigureAwait(false);
                if (server is not null) _store.MergeFromServer(server); // may fire Changed → one more coalesced pass
            } while (_again);
        }
        finally { _gate.Release(); }
    }

    private static bool IsPlaceholder(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name.Trim(), @"^Character \d+$");

    public void Dispose()
    {
        _store.Changed -= OnChanged;
        _periodic.Dispose();
        _gate.Dispose();
    }
}
