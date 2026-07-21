using System.Collections.Generic;
using System.IO;
using FujinTerm.Game.Leaderboard;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// LeaderboardSnapshotStore: per-BBS capture history, newest-first, bounded by
// MaxSnapshots. Mirrors RoomBlacklistStore's disk round-trip + Changed-event
// contract; these pin a scratch BBS under the shared DataRoot and clean it up.
public sealed class LeaderboardSnapshotStoreTests : IDisposable
{
    private readonly string _scratchBbs = "lb-test-" + Path.GetRandomFileName();
    private static readonly DateTimeOffset T = new(2026, 7, 20, 20, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try
        {
            string folder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private static LeaderboardSnapshot Snap(DateTimeOffset at, int requested, long exp)
        => new(at, requested, new[] { new LeaderboardEntry(1, "Salty Exp", "Bard", "None", exp) });

    [Fact]
    public void Add_InsertsNewestFirst_AndFiresChanged()
    {
        var store = new LeaderboardSnapshotStore();
        int fires = 0;
        store.Changed += () => fires++;

        store.Add(Snap(T.AddHours(-1), 100, 1000));
        store.Add(Snap(T, 100, 2000));

        Assert.Equal(2, fires);
        Assert.Equal(2, store.Snapshots.Count);
        Assert.Equal(2000, store.Snapshots[0].Entries[0].Experience); // newest first
    }

    [Fact]
    public void Add_TrimsBeyondMaxSnapshots()
    {
        var store = new LeaderboardSnapshotStore();
        for (int i = 0; i < 60; i++)
            store.Add(Snap(T.AddMinutes(i), 100, 1000 + i));

        // MaxSnapshots caps the history; the tail (oldest) falls off.
        Assert.Equal(50, store.Snapshots.Count);
        Assert.Equal(1059, store.Snapshots[0].Entries[0].Experience); // most recent retained
    }

    [Fact]
    public void Clear_EmptiesHistory_AndFires()
    {
        var store = new LeaderboardSnapshotStore();
        store.Add(Snap(T, 100, 1000));
        int fires = 0;
        store.Changed += () => fires++;

        store.Clear();

        Assert.Empty(store.Snapshots);
        Assert.Equal(1, fires);
    }

    [Fact]
    public void Persist_ReloadRoundTrip_PreservesEntriesAndRequestedCount()
    {
        var store = new LeaderboardSnapshotStore();
        store.OnBbsPinApplied(_scratchBbs);
        store.Add(Snap(T, 250, 1234567));

        Assert.True(File.Exists(AppPaths.BbsLeaderboardFile(_scratchBbs)));

        var reloaded = new LeaderboardSnapshotStore();
        reloaded.OnBbsPinApplied(_scratchBbs);

        Assert.Single(reloaded.Snapshots);
        LeaderboardSnapshot snap = reloaded.Snapshots[0];
        Assert.Equal(250, snap.RequestedCount);
        Assert.Equal(T, snap.CapturedAtUtc);
        Assert.Single(snap.Entries);
        Assert.Equal(1234567, snap.Entries[0].Experience);
        Assert.Equal("Salty Exp", snap.Entries[0].Name);
    }

    [Fact]
    public void OnBbsPinApplied_Blank_ClearsInMemory()
    {
        var store = new LeaderboardSnapshotStore();
        store.OnBbsPinApplied(_scratchBbs);
        store.Add(Snap(T, 100, 1000));
        Assert.Single(store.Snapshots);

        store.OnBbsPinApplied(null);
        Assert.Empty(store.Snapshots);
    }
}
