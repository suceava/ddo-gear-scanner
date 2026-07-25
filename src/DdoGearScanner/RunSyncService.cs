using DdoGearScanner.Model;

namespace DdoGearScanner;

public enum SyncState { Off, Syncing, Synced, Error }

/// <summary>Live sync status for the UI. Pending = unsynced count (while Syncing); Detail = error text.</summary>
public sealed record SyncStatus(SyncState State, int Pending = 0, string? Detail = null);

/// <summary>
/// Two-way cloud sync for dungeon runs. The PUSH half is an outbox: when a run is saved (added/edited) a
/// debounced drain batch-pushes every still-unsynced run and marks them synced; deletes propagate immediately.
/// The PULL half runs right after each drain — GET /runs and reconcile, so web edits/deletes on the account
/// flow back down (local runs.json becomes a cache; the server is the source of truth for history). A periodic
/// timer and an explicit <see cref="Start"/> (on app launch / after the key changes) re-run both halves, so
/// anything that failed offline gets picked up. Nothing here blocks the capture thread — everything runs on
/// background tasks and only touches the (locked) store to record results.
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

            // Pull half: bring down web edits/deletes. Runs even when there was nothing to push (pending==0).
            await PullAndReconcileAsync().ConfigureAwait(false);
            Report(new SyncStatus(SyncState.Synced));
        }
        finally { _drainGate.Release(); }
    }

    /// <summary>GET /runs and merge into the local cache. A null result (network error / non-200) is skipped —
    /// the store's reconcile never treats "no answer" as "the account is empty".</summary>
    private async Task PullAndReconcileAsync()
    {
        IReadOnlyList<RunRecord>? server = await _client.PullAsync().ConfigureAwait(false);
        if (server is not null) _store.ReconcileFromServer(server);
    }

    public void Dispose()
    {
        _store.RunSaved -= OnRunSaved;
        _store.RunRemoved -= OnRunRemoved;
        _periodic.Dispose();
        _drainGate.Dispose();
    }
}
