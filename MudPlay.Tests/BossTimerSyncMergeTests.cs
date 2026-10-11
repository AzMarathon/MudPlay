using System;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The merge-classification rules behind the sync window: a tracked boss we hold no timer
// for is adopted outright when one player was asked and is a pick when several could
// answer, a matching timer is a no-op, and a genuine disagreement (or an untracked
// boss) surfaces a manual pick. Plus the row-VM state transitions that decide whether
// a row shows the picker or a resolved status line.
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

    // The one outcome that writes without a pick is a pick unless the offer is the
    // answer of the one player a telepath asked: from anyone else, or with a request
    // out where several players listen (gang, say, broadcast), it is offered as a
    // choice. The outcomes that write nothing are the same whoever it is from.
    [Theory]
    [InlineData(false, BossTimerSyncViewModel.TimerMergeKind.Conflict)]
    [InlineData(true, BossTimerSyncViewModel.TimerMergeKind.AutoMerge)]
    internal void A_timer_we_would_adopt_outright_is_a_pick_unless_one_player_was_asked(
        bool askedOfSenderAlone, BossTimerSyncViewModel.TimerMergeKind expected)
    {
        Assert.Equal(expected, BossTimerSyncViewModel.Classify(ours: null, tracked: true, offer: T, askedOfSenderAlone));
        Assert.Equal(BossTimerSyncViewModel.TimerMergeKind.InSync,
            BossTimerSyncViewModel.Classify(ours: T, tracked: true, offer: T.AddSeconds(20), askedOfSenderAlone));
        Assert.Equal(BossTimerSyncViewModel.TimerMergeKind.Conflict,
            BossTimerSyncViewModel.Classify(ours: T, tracked: true, offer: T.AddHours(3), askedOfSenderAlone));
        Assert.Equal(BossTimerSyncViewModel.TimerMergeKind.Conflict,
            BossTimerSyncViewModel.Classify(ours: null, tracked: false, offer: T, askedOfSenderAlone));
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
    // nothing to keep, and once one is adopted there is.
    [Fact]
    public void The_default_pick_reads_take_none_until_we_hold_a_timer()
    {
        BossTimerSyncRowViewModel row = new("ok", 300, "ok", null, null, "— no timer —");
        Assert.Equal("Take none", row.KeepOursLabel);

        row.MarkAutoMerged("Bob", T, "Aug 24 00:13");

        Assert.Equal("Keep ours", row.KeepOursLabel);
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
