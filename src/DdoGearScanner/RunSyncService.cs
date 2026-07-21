using DdoGearScanner.Model;

namespace DdoGearScanner;

public enum SyncState { Off, Syncing, Synced, Error }

/// <summary>Live sync status for the UI. Pending = unsynced count (while Syncing); Detail = error text.</summary>
public sealed record SyncStatus(SyncState State, int Pending = 0, string? Detail = null);

/// <summary>
/// The cloud-sync outbox for dungeon runs. Local capture is unchanged and always authoritative; this rides
/// on top: when a run is saved (added/edited) it triggers a debounced drain that batch-pushes every
/// still-unsynced run and marks them synced; deletes propagate immediately (best-effort). A periodic timer
/// and an explicit <see cref="Start"/> (on app launch / after the key changes) re-drain, so anything that
/// failed while offline or key-less gets picked up. Nothing here blocks the capture thread — pushes run on
/// background tasks and only touch the (locked) store to mark results.
/// </summary>
public sealed class RunSyncService : IDisposable
{
    private const int BatchMax = 500; // CONTRACT.md POST /runs cap

    private readonly RunStore _store;
    private readonly RunSyncClient _client;
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly System.Timers.Timer _periodic;
    private volatile bool _drainRequested;

    /// <summary>Latest sync status; also pushed via <see cref="StatusChanged"/> whenever it changes.</summary>
    public SyncStatus Status { get; private set; } = new(SyncState.Off);
    public event Action<SyncStatus>? StatusChanged;

    private void Report(SyncStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }

    public RunSyncService(RunStore store, RunSyncClient client)
    {
        _store = store;
        _client = client;
        _store.RunSaved += OnRunSaved;
        _store.RunRemoved += OnRunRemoved;

        _periodic = new System.Timers.Timer(TimeSpan.FromMinutes(2).TotalMilliseconds) { AutoReset = true };
        _periodic.Elapsed += (_, _) => TriggerDrain();
        _periodic.Start();
    }

    /// <summary>Emit the current status and push any unsynced runs — call on startup and after the key changes.</summary>
    public void Start()
    {
        if (!_client.IsConfigured) { Report(new SyncStatus(SyncState.Off)); return; }
        int pending = _store.Unsynced().Count;
        Report(pending == 0 ? new SyncStatus(SyncState.Synced) : new SyncStatus(SyncState.Syncing, pending));
        TriggerDrain();
    }

    private void OnRunSaved(RunRecord run) => TriggerDrain();

    // Best-effort: if the delete fails the run is orphaned server-side (rare; a later cleanup could sweep it).
    private void OnRunRemoved(string id) => _ = _client.DeleteAsync(id);

    private void TriggerDrain() => _ = DrainAsync();

    private async Task DrainAsync()
    {
        if (!_client.IsConfigured) { Report(new SyncStatus(SyncState.Off)); return; }

        // One drain at a time; a trigger during a drain sets a flag so we loop once more (coalesces bursts).
        if (!await _drainGate.WaitAsync(0).ConfigureAwait(false))
        {
            _drainRequested = true;
            return;
        }
        try
        {
            do
            {
                _drainRequested = false;
                IReadOnlyList<RunRecord> pending = _store.Unsynced();
                if (pending.Count == 0) break;

                Report(new SyncStatus(SyncState.Syncing, pending.Count));
                foreach (RunRecord[] chunk in pending.Chunk(BatchMax))
                {
                    bool ok = await _client.PushAsync(chunk, default).ConfigureAwait(false);
                    if (!ok)
                    {
                        Report(new SyncStatus(SyncState.Error, _store.Unsynced().Count, "Sync failed — see log"));
                        return; // leave unsynced; the periodic timer / next save retries
                    }
                    foreach (RunRecord r in chunk) _store.MarkSynced(r.Id);
                }
            } while (_drainRequested);

            Report(new SyncStatus(SyncState.Synced));
        }
        finally { _drainGate.Release(); }
    }

    public void Dispose()
    {
        _store.RunSaved -= OnRunSaved;
        _store.RunRemoved -= OnRunRemoved;
        _periodic.Dispose();
        _drainGate.Dispose();
    }
}
