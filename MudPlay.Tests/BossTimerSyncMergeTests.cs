using System;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The merge-classification rules behind the sync window: a tracked boss we hold no timer
// for is adopted outright, a matching timer is a no-op, and only a genuine disagreement
// (or an untracked boss) surfaces a manual pick. Plus the row-VM state transitions that
// decide whether a row shows the picker or a resolved status line.
public sealed class BossTimerSyncMergeTests
{
    private static readonly DateTimeOffset T = new(2026, 8, 24, 0, 13, 0, TimeSpan.Zero);

    [Fact]
    public void Untracked_boss_is_always_a_manual_pick()
    {
        Assert.Equal(BossTimerSyncViewModel.TimerMergeKind.Conflict,
            BossTimerSyncViewModel.Classify(ours: null, tracked: false, offer: T));
        Assert.Equal(BossTimerSyncViewModel.TimerMergeKind.Conflict,
            BossTimerSyncViewModel.Classify(ours: T, tracked: false, offer: T));
    }

    [Fact]
    public void Tracked_boss_with_no_held_timer_auto_merges()
        => Assert.Equal(BossTimerSyncViewModel.TimerMergeKind.AutoMerge,
            BossTimerSyncViewModel.Classify(ours: null, tracked: true, offer: T));

    [Fact]
    public void Held_timer_matching_the_offer_is_in_sync()
        => Assert.Equal(BossTimerSyncViewModel.TimerMergeKind.InSync,
            BossTimerSyncViewModel.Classify(ours: T, tracked: true, offer: T.AddSeconds(20)));

    [Fact]
    public void Held_timer_disagreeing_with_the_offer_is_a_conflict()
        => Assert.Equal(BossTimerSyncViewModel.TimerMergeKind.Conflict,
            BossTimerSyncViewModel.Classify(ours: T, tracked: true, offer: T.AddHours(3)));

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    [InlineData(3600, false)]
    public void SameTimer_absorbs_a_minute_of_spread(int deltaSeconds, bool expectedSame)
    {
        Assert.Equal(expectedSame, BossTimerSyncViewModel.SameTimer(T, T.AddSeconds(deltaSeconds)));
        Assert.Equal(expectedSame, BossTimerSyncViewModel.SameTimer(T, T.AddSeconds(-deltaSeconds)));
    }

    [Fact]
    public void AutoMerged_row_shows_a_status_and_advances_our_timer()
    {
        BossTimerSyncRowViewModel row = new("adult red dragon", 100, "adult red dragon", null, null, "— no timer —");

        row.MarkAutoMerged("Fujin", T, "Aug 24 00:13 (0m ago)");

        Assert.False(row.HasConflict);
        Assert.True(row.WasAutoMerged);
        Assert.Equal(T, row.OursKilledAt);
        Assert.Contains("Fujin", row.ResolvedStatus);
        Assert.Empty(row.Responders);
    }

    [Fact]
    public void InSync_row_shows_a_status_and_no_picker()
    {
        BossTimerSyncRowViewModel row = new("alchemist", 200, "alchemist", null, T, "Aug 24 00:13");

        row.MarkInSync("Fujin");

        Assert.False(row.HasConflict);
        Assert.False(row.WasAutoMerged);
        Assert.Contains("in sync", row.ResolvedStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(row.Responders);
    }

    [Fact]
    public void Conflict_row_shows_a_pick_and_clears_any_prior_status()
    {
        BossTimerSyncRowViewModel row = new("ok", 300, "ok", null, T, "Aug 24 00:13");
        row.MarkInSync("Bob");   // Bob agreed

        row.AddConflict("Fujin", T.AddHours(3), "Aug 24 03:13");

        Assert.True(row.HasConflict);
        Assert.Empty(row.ResolvedStatus);
        Assert.Single(row.Responders);
        Assert.Equal("Fujin", row.Responders[0].Responder);
    }

    [Fact]
    public void A_resenders_pick_replaces_their_prior_one()
    {
        BossTimerSyncRowViewModel row = new("ok", 300, "ok", null, T, "Aug 24 00:13");
        row.AddConflict("Fujin", T.AddHours(3), "first");
        row.AddConflict("Fujin", T.AddHours(4), "second");

        Assert.Single(row.Responders);
        Assert.Equal(T.AddHours(4), row.Responders[0].KilledAt);
    }
}
