using MudPlay.Game.GameData;
using Xunit;

namespace MudPlay.Tests;

// RoomCommandSourceParser: the "Room(<command>) <rooms>" entries Nightmare Redux for
// Linux adds to an item's "Obtained From", told apart from the plain "Room M/R"
// entries and read safely out of empty and cut cells.
public sealed class RoomCommandSourceParserTests
{
    private const string CopperOre =
        "Shop(sell) #163, Textblock #1064(25%), Room(mine ore|mine vein|mine copper vein) "
        + "6/1664|6/1673|6/1679|6/1681|6/1701|6/1752|6/1754|6/1758|6/1770|6/1782|6/1795|6/1935|6/1944(25%)";

    private const string CoffinRooms =
        "17/278|17/287|17/310|17/330|17/338|17/2073-2096|17/2142-2174|17/2222-2234|17/2236|17/2238|17/2239|17/2241|17/2244-2258";

    private static readonly string VorpalSword =
        "Shop(nogen) #123, Shop(nogen) #167, Monster #878(50%), Textblock #898(2%), Item #907(5.9%), "
        + $"Room(pry coffin) {CoffinRooms}(18.6%), Room(tip coffin|push coffin) {CoffinRooms}(19%)";

    [Theory]
    [InlineData("\0")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Room 3/606, Textblock #475")]            // a plain "Room " is a different kind of entry
    [InlineData("Room(pry coffin) 17/278|17/28+\0")]      // the only entry is the cut one
    [InlineData("Room(dig 1/5")]
    [InlineData("Room() 1/5")]
    [InlineData("Room(dig) 1/9-5")]                       // a range never runs backwards
    [InlineData("Room(dig) 1/5(12)")]
    [InlineData("Room(dig)1/5")]
    [InlineData("room(dig) 1/5")]                         // case-sensitive
    [InlineData("Room(dig) 1/5 (12%)")]
    [InlineData("Room 1/5(25%)")]
    public void CellWithNoRoomCommandEntry_YieldsNone(string? cell)
        => Assert.Empty(RoomCommandSourceParser.ParseCell(cell));

    [Fact]
    public void DirectGive_HasNoChance()
    {
        RoomCommandSource s = Assert.Single(RoomCommandSourceParser.ParseCell(
            "Textblock #237, Room(say And conjure the arcane tome of Lord Morukai.) 1/157"));

        Assert.Equal(new[] { "say And conjure the arcane tome of Lord Morukai." }, s.Commands);
        Assert.Equal(new[] { (1, 157) }, s.Rooms);
        Assert.True(s.IsDirect);
    }

    [Theory]
    [InlineData("Room(dig) 1/5(0.04%)", 0.04, false)]
    [InlineData("Room(dig) 1/5(?%)", null, true)]
    [InlineData("Room(dig) 1/5(12%)", 12.0, false)]
    public void ChanceForms(string cell, double? chance, bool unknown)
    {
        RoomCommandSource s = Assert.Single(RoomCommandSourceParser.ParseCell(cell));

        Assert.Equal(chance, s.ChancePercent);
        Assert.Equal(unknown, s.ChanceUnknown);
        Assert.False(s.IsDirect);
    }

    [Fact]
    public void SynonymsAndRanges_AreExpanded()
    {
        RoomCommandSource s = Assert.Single(RoomCommandSourceParser.ParseCell("Room(a|b c|d) 1/5|1/7-9|2/10(12%)"));

        Assert.Equal(new[] { "a", "b c", "d" }, s.Commands);
        Assert.Equal(new[] { (1, 5), (1, 7), (1, 8), (1, 9), (2, 10) }, s.Rooms);
    }

    [Fact]
    public void SanitisedCommandCharacters_AreKeptAsWritten()
    {
        RoomCommandSource s = Assert.Single(RoomCommandSourceParser.ParseCell("Room(say hi; [old] friend/now) 4/1"));

        Assert.Equal(new[] { "say hi; [old] friend/now" }, s.Commands);
    }

    // A cell too long for its column is cut mid-entry and ends "+" NUL. Half an entry
    // must not read as a room of its own ("17/27" out of "17/278").
    [Fact]
    public void CutCell_DropsItsLastEntry_KeepsTheRest()
    {
        RoomCommandSource s = Assert.Single(RoomCommandSourceParser.ParseCell(
            "Shop #5, Room(pry coffin) 17/278|17/2073-2096(18.6%), Room(tip coffin|push coffin) 17/27+\0"));

        Assert.Equal(new[] { "pry coffin" }, s.Commands);
        Assert.Equal(25, s.Rooms.Count);
        Assert.Equal(18.6, s.ChancePercent);

        Assert.Equal(new[] { "get roots" },
            Assert.Single(RoomCommandSourceParser.ParseCell("Room(get roots) 9/500, Room(pry cof+\0")).Commands);
        Assert.Single(RoomCommandSourceParser.ParseCell("Room(dig) 1/5, +\0"));
    }

    [Fact]
    public void RealCells_CopperOreAndVorpalSword()
    {
        RoomCommandSource ore = Assert.Single(RoomCommandSourceParser.ParseCell(CopperOre));
        Assert.Equal(new[] { "mine ore", "mine vein", "mine copper vein" }, ore.Commands);
        Assert.Equal(13, ore.Rooms.Count);
        Assert.Equal((6, 1664), ore.Rooms[0]);
        Assert.Equal((6, 1944), ore.Rooms[^1]);
        Assert.Equal(25, ore.ChancePercent);

        // Prying and tipping a coffin draw from different tables: two entries over the
        // same 94 rooms, never merged into one chance.
        List<RoomCommandSource> sword = RoomCommandSourceParser.ParseCell(VorpalSword);
        Assert.Equal(2, sword.Count);
        Assert.All(sword, s => Assert.Equal(94, s.Rooms.Count));
        Assert.Equal(18.6, sword[0].ChancePercent);
        Assert.Equal(new[] { "tip coffin", "push coffin" }, sword[1].Commands);
        Assert.Equal(19, sword[1].ChancePercent);
    }

    [Fact]
    public void Entries_SplitsACellAndDropsTheCutTail()
    {
        Assert.Equal(new[] { "Room 3/606", "Textblock #475" }, RoomCommandSourceParser.Entries("Room 3/606, Textblock #475"));
        Assert.Equal(new[] { "Room 7/308", "Room 7/309" }, RoomCommandSourceParser.Entries("Room 7/308, Room 7/309, Roo+\0"));
        Assert.Empty(RoomCommandSourceParser.Entries("\0"));
    }
}
