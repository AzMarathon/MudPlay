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

    // What an offer is to the requests the user made decides the two outcomes that
    // write without a pick. None held: merged in when it answers a request of the
    // user's, listed when it answers none. Differs from ours: synced when it is the
    // answer of the one player asked, listed otherwise. The same timer, and a boss
    // off our list, come out the same whoever it is from.
    [Theory]
    [InlineData(MudPlay.Game.Remote.TimerSyncStanding.TheOneAsked,
        BossTimerSyncViewModel.TimerMergeKind.AutoMerge, BossTimerSyncViewModel.TimerMergeKind.Sync)]
    [InlineData(MudPlay.Game.Remote.TimerSyncStanding.OneOfSeveral,
        BossTimerSyncViewModel.TimerMergeKind.AutoMerge, BossTimerSyncViewModel.TimerMergeKind.Conflict)]
    [InlineData(MudPlay.Game.Remote.TimerSyncStanding.NotAsked,
        BossTimerSyncViewModel.TimerMergeKind.Conflict, BossTimerSyncViewModel.TimerMergeKind.Conflict)]
    internal void What_is_written_without_a_pick_goes_by_what_the_offer_answers(
        MudPlay.Game.Remote.TimerSyncStanding standing,
        BossTimerSyncViewModel.TimerMergeKind noneHeld, BossTimerSyncViewModel.TimerMergeKind differs)
    {
        Assert.Equal(noneHeld, BossTimerSyncViewModel.Classify(ours: null, tracked: true, offer: T, standing));
        Assert.Equal(differs, BossTimerSyncViewModel.Classify(ours: T, tracked: true, offer: T.AddHours(3), standing));
        Assert.Equal(BossTimerSyncViewModel.TimerMergeKind.InSync,
            BossTimerSyncViewModel.Classify(ours: T, tracked: true, offer: T.AddSeconds(20), standing));
        Assert.Equal(BossTimerSyncViewModel.TimerMergeKind.Conflict,
            BossTimerSyncViewModel.Classify(ours: null, tracked: false, offer: T, standing));
        Assert.Equal(BossTimerSyncViewModel.TimerMergeKind.Conflict,
            BossTimerSyncViewModel.Classify(ours: T, tracked: false, offer: T.AddHours(3), standing));
    }

    // A row brought up to a timer of ours that moved: the default pick is chosen
    // again, and what matched the old timer is no longer said to.
    [Fact]
    public void A_rebased_row_goes_back_to_the_default_pick_against_the_new_timer()
    {
        BossTimerSyncRowViewModel row = new("ok", 300, "ok", null, null, "— no timer —");
        row.AddConflict("Bob", T, "Aug 24 00:13");
        row.Responders[0].AdoptCommand.Execute(null);
        Assert.Equal(T, row.SelectedKilledAt);

        row.Rebase(T.AddHours(6), "Aug 24 06:13");

        Assert.Null(row.SelectedKilledAt);
        Assert.True(row.KeepOursSelected);
        Assert.False(row.Responders[0].IsSelected);
        Assert.Equal("Keep ours", row.KeepOursLabel);
        Assert.Equal(T.AddHours(6), row.OursKilledAt);
    }

    // The default pick is headed by what it does: with no timer of ours there is
    // nothing to keep, and once one is merged in there is, shown with whose it was.
    [Fact]
    public void The_default_pick_reads_take_none_until_we_hold_a_timer()
    {
        BossTimerSyncRowViewModel row = new("ok", 300, "ok", null, null, "— no timer —");
        Assert.Equal("Take none", row.KeepOursLabel);

        row.MarkAutoMerged("Bob", T, "Aug 24 00:13");

        Assert.Equal("Keep ours", row.KeepOursLabel);
        Assert.Equal("Aug 24 00:13 · merged in from Bob", row.OursText);
    }

    // A synced row holds the responder's timer as ours and says whose it was. What
    // had matched the timer it replaced is no longer said to match.
    [Fact]
    public void Synced_row_shows_a_status_and_holds_the_new_timer()
    {
        BossTimerSyncRowViewModel row = new("ok", 300, "ok", null, T, "Aug 24 00:13");
        row.MarkInSync("Sue");

        row.MarkSynced("Bob", T.AddHours(3), "Aug 24 03:13");

        Assert.False(row.HasConflict);
        Assert.True(row.WasSynced);
        Assert.False(row.WasAutoMerged);
        Assert.Equal(T.AddHours(3), row.OursKilledAt);
        Assert.Equal("Synced to Bob's timer — Aug 24 03:13", row.ResolvedStatus);
        Assert.Equal("Aug 24 03:13 · synced from Bob", row.OursText);
        Assert.Empty(row.Responders);

        row.MarkInSync("Ann");
        Assert.Equal("Synced to Bob's timer — Aug 24 03:13", row.ResolvedStatus);
    }

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
