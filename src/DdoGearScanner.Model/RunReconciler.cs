namespace DdoGearScanner.Model;

/// <summary>
/// The pure merge at the heart of run sync's PULL half (kept out of the store so it's I/O-free and unit
/// testable). Given the local runs and the server's runs, produces the reconciled set. Rules — the server is
/// the source of truth for HISTORY, local is a cache + outbox:
///  - local not-yet-pushed (Synced=false) → KEEP (outbox wins until it pushes; a server copy is ignored).
///  - local synced &amp; on server → ADOPT the server's version (a web edit), keeping only the local-only
///    CharacterId. EnteredUtc comes from the server too, so it and CompletedUtc share one precision (else a
///    web time-edit renders a second short — see the SameSecond note / the adopt site).
///  - local synced &amp; ABSENT from server → a web delete → DROP.
///  - server-only → ADD (restores history on a fresh install / another PC).
/// SAFETY: an EMPTY server list never deletes anything — that guards a bad key or a first sync (lots of
/// unsynced local, nothing server-side yet) from wiping local history. The caller must also only pass a REAL
/// 200 result (never a failed fetch).
/// </summary>
public static class RunReconciler
{
    public readonly record struct Result(IReadOnlyList<RunRecord> Runs, bool Changed);

    public static Result Merge(IReadOnlyList<RunRecord> local, IReadOnlyList<RunRecord> server)
    {
        var byId = new Dictionary<string, RunRecord>(server.Count);
        foreach (RunRecord r in server) byId[r.Id] = r;
        bool serverEmpty = byId.Count == 0;

        var next = new List<RunRecord>(local.Count);
        bool changed = false;
        foreach (RunRecord l in local)
        {
            if (!l.Synced)
            {
                next.Add(l);            // outbox — untouched, and don't also re-add a server copy
                byId.Remove(l.Id);
                continue;
            }
            if (byId.TryGetValue(l.Id, out RunRecord? srv))
            {
                // Adopt the server's version, but keep the local-only CharacterId (the wire never carries it).
                // We take the server's EnteredUtc too — NOT the local one — so EnteredUtc and CompletedUtc come
                // from the same (millisecond) source. Mixing local sub-ms EnteredUtc with a server CompletedUtc
                // (which the web built off the ms-truncated enteredUtc) left the duration a hair short, and the
                // desktop's truncating m:ss format then dropped a whole second (e.g. an edited 40:34 → 40:33).
                RunRecord adopted = srv with { CharacterId = l.CharacterId ?? srv.CharacterId };
                if (!SyncEqual(adopted, l)) changed = true;
                next.Add(adopted);
                byId.Remove(l.Id);
            }
            else if (serverEmpty)
            {
                next.Add(l);            // suspicious-empty guard: never mass-delete on an empty response
            }
            else
            {
                changed = true;         // synced locally, gone server-side → web-deleted → drop
            }
        }
        foreach (RunRecord srv in byId.Values) { next.Add(srv); changed = true; } // server-only → adopt

        return new Result(next, changed);
    }

    /// <summary>Same run for sync purposes — ignores local-only fields (CharacterId, transient Paused) and
    /// compares CompletedUtc at whole-second granularity so the wire's millisecond rounding doesn't look like
    /// an endless "change" on every pull.</summary>
    private static bool SyncEqual(RunRecord a, RunRecord b)
        => a.DungeonName == b.DungeonName && a.Difficulty == b.Difficulty && a.CharacterLevel == b.CharacterLevel
           && a.Xp == b.Xp && a.Completed == b.Completed && a.Edited == b.Edited && a.QuestLevel == b.QuestLevel
           && a.CharacterName == b.CharacterName && a.QuestDuration == b.QuestDuration
           && SameSecond(a.CompletedUtc, b.CompletedUtc);

    private static bool SameSecond(DateTime? a, DateTime? b)
        => a is null ? b is null : b is not null && Math.Abs((a.Value - b.Value).TotalSeconds) < 1.0;
}
