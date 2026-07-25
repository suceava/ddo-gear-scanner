using DdoGearScanner.Model;
using Xunit;

namespace DdoGearScanner.Vision.Tests;

/// <summary>
/// The pull-half merge — the safety-critical part of two-way run sync. These pin the rules that keep a web
/// edit/delete flowing to the desktop WITHOUT ever letting a bad/empty fetch wipe local history.
/// </summary>
public class RunReconcilerTests
{
    private static readonly DateTime Entered = new(2026, 7, 10, 18, 20, 0, DateTimeKind.Utc);

    private static RunRecord Run(string id, bool synced, string dungeon = "The Pit", int? xp = 21450,
        string? charId = "c1", string? charName = "Throgar") =>
        new(Id: id, DungeonName: dungeon, Difficulty: "Elite", CharacterLevel: 20, CharacterId: charId,
            EnteredUtc: Entered, CompletedUtc: Entered.AddMinutes(21), Xp: xp, Completed: true, RawOcrText: "",
            QuestLevel: 16, CharacterName: charName, Synced: synced);

    // A run "as it comes back from the server": no CharacterId (local-only), always Synced.
    private static RunRecord FromServer(string id, string dungeon = "The Pit", int? xp = 21450, string? charName = "Throgar") =>
        Run(id, synced: true, dungeon: dungeon, xp: xp, charId: null, charName: charName);

    [Fact]
    public void FirstSync_manyUnsyncedLocal_emptyServer_deletesNothing()
    {
        // The scenario the user flagged: key just added, lots of local records, GET returns nothing.
        var local = new[] { Run("a", synced: false), Run("b", synced: false), Run("c", synced: false) };
        RunReconciler.Result r = RunReconciler.Merge(local, Array.Empty<RunRecord>());
        Assert.Equal(3, r.Runs.Count);
        Assert.False(r.Changed);
    }

    [Fact]
    public void EmptyServer_neverDeletesEvenSyncedLocal()
    {
        // A bad key / backend glitch returns empty — synced local history must survive.
        var local = new[] { Run("a", synced: true), Run("b", synced: true) };
        RunReconciler.Result r = RunReconciler.Merge(local, Array.Empty<RunRecord>());
        Assert.Equal(2, r.Runs.Count);
        Assert.False(r.Changed);
    }

    [Fact]
    public void AdoptsServerEdit_forSyncedRun_preservingCharacterId()
    {
        var local = new[] { Run("a", synced: true, xp: 21450, charId: "c1") };
        var server = new[] { FromServer("a", xp: 99999) }; // web edited the XP
        RunReconciler.Result r = RunReconciler.Merge(local, server);
        Assert.True(r.Changed);
        RunRecord merged = Assert.Single(r.Runs);
        Assert.Equal(99999, merged.Xp);
        Assert.Equal("c1", merged.CharacterId);      // local-only field kept
        Assert.Equal(Entered, merged.EnteredUtc);     // immutable anchor kept
        Assert.True(merged.Synced);
    }

    [Fact]
    public void NoChange_whenSyncedRunMatchesServer()
    {
        var local = new[] { Run("a", synced: true) };
        var server = new[] { FromServer("a") };
        RunReconciler.Result r = RunReconciler.Merge(local, server);
        Assert.False(r.Changed);                       // identical content → no reload/save churn
        Assert.Single(r.Runs);
    }

    [Fact]
    public void DropsSyncedRun_absentFromNonEmptyServer_webDelete()
    {
        var local = new[] { Run("a", synced: true), Run("b", synced: true) };
        var server = new[] { FromServer("a") };        // "b" was deleted on the web
        RunReconciler.Result r = RunReconciler.Merge(local, server);
        Assert.True(r.Changed);
        Assert.Single(r.Runs);
        Assert.Equal("a", r.Runs[0].Id);
    }

    [Fact]
    public void KeepsUnsyncedLocal_absentFromServer_pendingPush()
    {
        // A brand-new local run not yet pushed must never be treated as a web delete.
        var local = new[] { Run("a", synced: true), Run("new", synced: false) };
        var server = new[] { FromServer("a") };
        RunReconciler.Result r = RunReconciler.Merge(local, server);
        Assert.Contains(r.Runs, x => x.Id == "new");
        Assert.False(r.Runs.Single(x => x.Id == "new").Synced);
    }

    [Fact]
    public void UnsyncedLocalWins_overServerCopy_noDuplicate()
    {
        // Locally edited (dirty) but the server still has the old copy — keep the dirty local, once, not both.
        var local = new[] { Run("a", synced: false, xp: 50000) };
        var server = new[] { FromServer("a", xp: 21450) };
        RunReconciler.Result r = RunReconciler.Merge(local, server);
        RunRecord kept = Assert.Single(r.Runs);
        Assert.Equal(50000, kept.Xp);
        Assert.False(kept.Synced);
    }

    [Fact]
    public void AddsServerOnlyRun_freshInstallRestore()
    {
        var local = Array.Empty<RunRecord>();
        var server = new[] { FromServer("a"), FromServer("b") };
        RunReconciler.Result r = RunReconciler.Merge(local, server);
        Assert.True(r.Changed);
        Assert.Equal(2, r.Runs.Count);
        Assert.All(r.Runs, x => Assert.True(x.Synced));
    }

    [Fact]
    public void MillisecondRounding_onCompletedUtc_isNotAChange()
    {
        var local = new[] { Run("a", synced: true) with { CompletedUtc = Entered.AddMinutes(21).AddMilliseconds(370) } };
        var server = new[] { FromServer("a") };        // server rounded to the second
        RunReconciler.Result r = RunReconciler.Merge(local, server);
        Assert.False(r.Changed);                       // sub-second diff must not churn every pull
    }
}
