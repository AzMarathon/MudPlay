using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Two lair-tag parsers exist and they are NOT interchangeable. This pins which
// shapes each one handles, because picking the wrong one fails silently — it
// reports "no monsters here" rather than throwing, which read as "these loops
// have no lairs" across an entire realm.
public sealed class LairTagShapeTests
{
    // The shape this realm's Rooms.Lair cells actually use: a max-regen header,
    // the monster ids, then the spawn-group index in trailing brackets.
    private const string MaxHeaderShape = "(Max 1): 53,54,55,56,58,123,124,125,752,753,754,[10-10-15-1]";

    [Fact]
    public void ParseLairTag_ReadsTheMaxHeaderShape()
    {
        RoomTooltipBuilder.ParseLairTag(MaxHeaderShape, out int? max, out IReadOnlyList<int> ids);

        Assert.Equal(1, max);
        Assert.Equal(new[] { 53, 54, 55, 56, 58, 123, 124, 125, 752, 753, 754 }, ids);
    }

    [Fact]
    public void ParseLairTag_DoesNotMistakeTheGroupIndexForMonsterIds()
    {
        // "[10-10-15-1]" is a spawn-rule key, not a mob list. Leaking those
        // numbers in would invent monsters that aren't there.
        RoomTooltipBuilder.ParseLairTag("(Max 2): 503,[30-24-24-2]", out _, out IReadOnlyList<int> ids);
        Assert.Equal(new[] { 503 }, ids);
    }

    [Fact]
    public void LairTagParser_ReturnsNullForTheMaxHeaderShape()
    {
        // The reason LocalApiCatalog uses RoomTooltipBuilder instead. LairTagParser
        // understands the NMR 1.83+ group form and a bare pre-1.83 id list — not
        // this one — and a null here is indistinguishable from "room has no lair"
        // unless you know to look.
        Assert.Null(LairTagParser.TryParse(MaxHeaderShape));
    }

    [Fact]
    public void LairTagParser_HandlesTheShapesItDoesClaim()
    {
        // NMR 1.83+: carries a group key and NO ids (those live in Lairs.json), so
        // an ids-only consumer gets nothing even on a successful parse.
        LairTagInfo? group = LairTagParser.TryParse("[10-10-15-1][3]Group(lair): 7/3");
        Assert.NotNull(group);
        Assert.Equal("10-10-15-1", group!.GroupIndex);
        Assert.Equal(3, group.MaxRegen);
        Assert.Empty(group.MonsterIds);

        // Pre-1.83: a bare id list.
        LairTagInfo? list = LairTagParser.TryParse("12, 34, 56");
        Assert.NotNull(list);
        Assert.Equal(new[] { 12, 34, 56 }, list!.MonsterIds);
    }

    [Fact]
    public void BothParsers_TolerateEmptyAndGarbage()
    {
        Assert.Null(LairTagParser.TryParse(null));
        Assert.Null(LairTagParser.TryParse("   "));

        RoomTooltipBuilder.ParseLairTag("", out int? max, out IReadOnlyList<int> ids);
        Assert.Null(max);
        Assert.Empty(ids);
    }
}
