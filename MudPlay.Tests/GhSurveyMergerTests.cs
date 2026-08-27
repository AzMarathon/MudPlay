using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins GhSurveyMerger's core invariant — report 20260827 ("9 hidden items:
// search finds 7, then 5, then 3, and the total looks like it's adding those
// up"). Repeated `sea` commands can rediscover the same physical stack with a
// fluctuating apparent count (the game's hidden-search reveal isn't a stable
// full re-list), so every merge here MUST settle on the highest count any
// single search reported, never a sum across rounds — this class had no
// dedicated test before, despite SearchesPerRoom > 1 being a real, commonly
// configured setting.
public sealed class GhSurveyMergerTests
{
    private static ItemNameStore NoGameData() => new(new GameDataCache());

    [Fact]
    public void Merge_DecreasingCountsAcrossRepeatedSearches_SettlesOnMax()
    {
        ItemNameStore names = NoGameData();
        var observed = new Dictionary<RoomKey, List<string>>();
        RoomKey room = new(6, 3471);

        GhSurveyMerger.Merge(observed, room, new[] { "7 gold coins" }, names);
        GhSurveyMerger.Merge(observed, room, new[] { "5 gold coins" }, names);
        GhSurveyMerger.Merge(observed, room, new[] { "3 gold coins" }, names);

        Assert.Equal(new[] { "7 gold coins" }, observed[room]);
    }

    [Fact]
    public void Merge_IncreasingCountsAcrossRepeatedSearches_SettlesOnMax()
    {
        // Reverse order of the above — proves this is a real max, not just
        // "whichever search happened first" or "whichever happened last".
        ItemNameStore names = NoGameData();
        var observed = new Dictionary<RoomKey, List<string>>();
        RoomKey room = new(6, 3471);

        GhSurveyMerger.Merge(observed, room, new[] { "3 gold coins" }, names);
        GhSurveyMerger.Merge(observed, room, new[] { "5 gold coins" }, names);
        GhSurveyMerger.Merge(observed, room, new[] { "7 gold coins" }, names);

        Assert.Equal(new[] { "7 gold coins" }, observed[room]);
    }

    [Fact]
    public void Merge_DifferentRooms_TrackedIndependently_NeverCombined()
    {
        ItemNameStore names = NoGameData();
        var observed = new Dictionary<RoomKey, List<string>>();

        GhSurveyMerger.Merge(observed, new RoomKey(6, 3454), new[] { "4 torches" }, names);
        GhSurveyMerger.Merge(observed, new RoomKey(6, 3469), new[] { "4 torches" }, names);

        Assert.Equal(new[] { "4 torches" }, observed[new RoomKey(6, 3454)]);
        Assert.Equal(new[] { "4 torches" }, observed[new RoomKey(6, 3469)]);
    }

    // Mirrors GhSweepManager.OnSurveyUpdated's actual hidden-item path: each
    // search round's `incoming` is the WHOLE floor (visible + whatever hidden
    // stock that round revealed), diffed against the room's pre-search visible
    // baseline, then folded into the hidden ledger via Merge — same
    // fluctuating-count scenario, but through the delta step recon really uses.
    [Fact]
    public void MergeHiddenDelta_FluctuatingRevealAcrossSearchRounds_SettlesOnMax()
    {
        ItemNameStore names = NoGameData();
        RoomKey room = new(6, 3471);
        var visible = new Dictionary<RoomKey, List<string>> { [room] = new() { "a torch" } };
        var hidden = new Dictionary<RoomKey, List<string>>();

        GhSurveyMerger.MergeHiddenDelta(hidden, room, new[] { "a torch", "7 gold coins" }, visible, names);
        GhSurveyMerger.MergeHiddenDelta(hidden, room, new[] { "a torch", "5 gold coins" }, visible, names);
        GhSurveyMerger.MergeHiddenDelta(hidden, room, new[] { "a torch", "3 gold coins" }, visible, names);

        Assert.Equal(new[] { "7 gold coins" }, hidden[room]);
    }

    [Fact]
    public void Canonical_StripsLeadingCount()
    {
        ItemNameStore names = NoGameData();
        Assert.Equal("gold coins", GhSurveyMerger.Canonical("7 gold coins", names));
    }

    // A recorded entry's exact casing can drift between search replies (the
    // game isn't guaranteed to echo identical casing every time) — Merge must
    // still recognize these as the same physical stack rather than tracking
    // them as two separate items whose counts both survive.
    [Fact]
    public void Merge_CaseDifferingEntries_TreatedAsSameItem()
    {
        ItemNameStore names = NoGameData();
        var observed = new Dictionary<RoomKey, List<string>>();
        RoomKey room = new(6, 3471);

        GhSurveyMerger.Merge(observed, room, new[] { "7 Gold Coins" }, names);
        GhSurveyMerger.Merge(observed, room, new[] { "5 gold coins" }, names);

        Assert.Single(observed[room]);
        Assert.Equal(7, observed[room].Sum(e => CountedCommand.SplitLeadingCount(e).Count));
    }
}
