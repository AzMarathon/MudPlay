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

    // The path recon takes for a list it can't tell from a redisplay: names the
    // display already showed are left out, and the rest fold into the hidden
    // ledger via Merge — the same fluctuating-count scenario through that step.
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

    // The numbers of report paradigm-20261009-164508: a room whose display showed 34
    // rope and grapple, 10 pulsating heart and a scorpion tail answered its searches
    // with hidden stacks of the same three and two more, the black diamonds as 10, 8
    // and 9 and one reply without the heart. The hidden stacks are their own copies,
    // so each ledger keeps its highest count and the room holds the two added.
    [Fact]
    public void Total_AddsTheHiddenStacksToTheVisibleOnes_EachAtItsHighestCount()
    {
        ItemNameStore names = NoGameData();
        RoomKey room = new(6, 3468);
        var visible = new Dictionary<RoomKey, List<string>>();
        var hidden = new Dictionary<RoomKey, List<string>>();

        GhSurveyMerger.Merge(visible, room,
            new[] { "34 rope and grapple", "10 pulsating heart", "scorpion tail" }, names);
        GhSurveyMerger.Merge(hidden, room, new[]
            { "2 wooden skiff", "2 rope and grapple", "scorpion tail", "pulsating heart", "10 black diamond" }, names);
        GhSurveyMerger.Merge(hidden, room, new[]
            { "2 wooden skiff", "2 rope and grapple", "scorpion tail", "8 black diamond" }, names);
        GhSurveyMerger.Merge(hidden, room, new[]
            { "2 wooden skiff", "2 rope and grapple", "scorpion tail", "pulsating heart", "9 black diamond" }, names);

        Assert.Equal(
            new[] { "2 wooden skiff", "2 rope and grapple", "scorpion tail", "pulsating heart", "10 black diamond" },
            hidden[room]);
        Assert.Equal(
            new[] { "36 rope and grapple", "11 pulsating heart", "2 scorpion tail", "2 wooden skiff", "10 black diamond" },
            GhSurveyMerger.Total(visible, hidden, room, names));
    }

    // A second display of the room is the same visible stacks again, not more of them.
    [Fact]
    public void Total_ARedisplayAddsNothing()
    {
        ItemNameStore names = NoGameData();
        RoomKey room = new(6, 3468);
        var visible = new Dictionary<RoomKey, List<string>>();
        var hidden = new Dictionary<RoomKey, List<string>>();

        GhSurveyMerger.Merge(visible, room, new[] { "34 rope and grapple" }, names);
        GhSurveyMerger.Merge(hidden, room, new[] { "2 rope and grapple" }, names);
        GhSurveyMerger.Merge(visible, room, new[] { "34 rope and grapple" }, names);

        Assert.Equal(new[] { "36 rope and grapple" }, GhSurveyMerger.Total(visible, hidden, room, names));
    }

    // A list that can't be told from a redisplay adds nothing to a stack the display
    // showed: the higher count stands, as it did before the two were told apart.
    [Fact]
    public void MergeUnattributed_KeepsTheHigherCountOfAVisibleStack_AndTakesNewNamesAsHidden()
    {
        ItemNameStore names = NoGameData();
        RoomKey room = new(6, 3468);
        var visible = new Dictionary<RoomKey, List<string>>
            { [room] = new() { "34 rope and grapple", "2 log raft" } };
        var hidden = new Dictionary<RoomKey, List<string>>();

        GhSurveyMerger.MergeUnattributed(visible, hidden, room,
            new[] { "2 rope and grapple", "5 log raft", "10 black diamond" }, names);

        Assert.Equal(new[] { "34 rope and grapple", "5 log raft" }, visible[room]);
        Assert.Equal(new[] { "10 black diamond" }, hidden[room]);
        Assert.Equal(
            new[] { "34 rope and grapple", "5 log raft", "10 black diamond" },
            GhSurveyMerger.Total(visible, hidden, room, names));
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
