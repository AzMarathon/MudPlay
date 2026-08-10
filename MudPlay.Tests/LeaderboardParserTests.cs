using System.Text;
using MudPlay.Game.Leaderboard;
using Xunit;

namespace MudPlay.Tests;

// LeaderboardParser: the "top N" listing is fixed-width and both Name and
// Gang/Guild carry spaces, so columns are derived from the header row's label
// positions rather than a whitespace split. These build the header and rows at
// the real stock column starts (Name@5, Class@27, Gang/Guild@38, Experience@58)
// so the header-driven slicing is exercised exactly as it runs on the terminal.
public sealed class LeaderboardParserTests
{
    private const int NameCol = 5, ClassCol = 27, GuildCol = 38, ExpCol = 58;

    private static string BuildHeader()
    {
        var sb = new StringBuilder();
        Place(sb, "Rank", 0);
        Place(sb, "Name", NameCol);
        Place(sb, "Class", ClassCol);
        Place(sb, "Gang/Guild", GuildCol);
        Place(sb, "Experience", ExpCol);
        return sb.ToString();
    }

    // Format a data row at the header's column starts: rank right-justified to a
    // 3-wide field with a trailing dot ("  1.", " 10.", "100."), then the fields.
    private static string Row(int rank, string name, string cls, string guild, string exp)
    {
        var sb = new StringBuilder();
        sb.Append((rank.ToString() + ".").PadLeft(4)); // occupies [0..4)
        Place(sb, name, NameCol);
        Place(sb, cls, ClassCol);
        Place(sb, guild, GuildCol);
        Place(sb, exp, ExpCol);
        return sb.ToString();
    }

    private static void Place(StringBuilder sb, string value, int start)
    {
        while (sb.Length < start) sb.Append(' ');
        sb.Append(value);
    }

    [Fact]
    public void TryParseHeader_DerivesColumnStarts()
    {
        Assert.True(LeaderboardParser.TryParseHeader(BuildHeader(), out LeaderboardParser.Columns c));
        Assert.Equal(NameCol, c.NameStart);
        Assert.Equal(ClassCol, c.ClassStart);
        Assert.Equal(GuildCol, c.GuildStart);
        Assert.Equal(ExpCol, c.ExpStart);
    }

    [Fact]
    public void TryParseHeader_RejectsNonHeaderLines()
    {
        Assert.False(LeaderboardParser.TryParseHeader(
            "=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-", out _));
        Assert.False(LeaderboardParser.TryParseHeader(
            Row(1, "Salty Exp", "Bard", "Droppin' Loads", "1529225118"), out _));
        Assert.False(LeaderboardParser.TryParseHeader("", out _));
    }

    [Fact]
    public void TryParseRow_ParsesEveryField()
    {
        LeaderboardParser.TryParseHeader(BuildHeader(), out LeaderboardParser.Columns c);
        Assert.True(LeaderboardParser.TryParseRow(
            Row(1, "Salty Exp", "Bard", "Droppin' Loads", "1529225118"), c, out LeaderboardEntry e));

        Assert.Equal(1, e.Rank);
        Assert.Equal("Salty Exp", e.Name);
        Assert.Equal("Bard", e.Class);
        Assert.Equal("Droppin' Loads", e.Guild); // apostrophe + space survive
        Assert.Equal(1529225118L, e.Experience);
        Assert.Equal("Salty", e.FirstName);
    }

    [Fact]
    public void TryParseRow_HandlesRankOneHundred_AndGuildNone()
    {
        LeaderboardParser.TryParseHeader(BuildHeader(), out LeaderboardParser.Columns c);
        Assert.True(LeaderboardParser.TryParseRow(
            Row(100, "MudButt Nibbler", "Warlock", "None", "4200"), c, out LeaderboardEntry e));

        Assert.Equal(100, e.Rank);
        Assert.Equal("MudButt Nibbler", e.Name);
        Assert.Equal("Warlock", e.Class);
        Assert.Equal("None", e.Guild);
        Assert.Equal(4200L, e.Experience);
    }

    [Fact]
    public void TryParseRow_LongestClassAndGuild_DontBleedColumns()
    {
        LeaderboardParser.TryParseHeader(BuildHeader(), out LeaderboardParser.Columns c);
        // "Missionary" (10) fills the class column; "Satanic Leprechauns" (19)
        // fills the guild column — the widest real values on the sampled board.
        Assert.True(LeaderboardParser.TryParseRow(
            Row(40, "Anubis ProtectorOf", "Witchunter", "Satanic Leprechauns", "8077241"), c, out LeaderboardEntry e));
        Assert.Equal("Anubis ProtectorOf", e.Name);
        Assert.Equal("Witchunter", e.Class);
        Assert.Equal("Satanic Leprechauns", e.Guild);
        Assert.Equal(8077241L, e.Experience);
    }

    [Fact]
    public void TryParseRow_TruncatedNameAbuttingClass_StaysInNameColumn()
    {
        LeaderboardParser.TryParseHeader(BuildHeader(), out LeaderboardParser.Columns c);
        // A 22-char name fills the name column right up to the class boundary with
        // no gap — the slice must not swallow the class token.
        Assert.True(LeaderboardParser.TryParseRow(
            Row(6, "Lenneth BoxOfRocksDumb", "Warrior", "what happen", "1066938013"), c, out LeaderboardEntry e));
        Assert.Equal("Lenneth BoxOfRocksDumb", e.Name);
        Assert.Equal("Warrior", e.Class);
    }

    [Fact]
    public void TryParseRow_RejectsSeparatorPromptAndBlank()
    {
        LeaderboardParser.TryParseHeader(BuildHeader(), out LeaderboardParser.Columns c);
        Assert.False(LeaderboardParser.TryParseRow(
            "=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-", c, out _));
        Assert.False(LeaderboardParser.TryParseRow("[HP=91/KAI=10]:", c, out _));
        Assert.False(LeaderboardParser.TryParseRow("", c, out _));
    }

    [Theory]
    [InlineData("top 100", 100)]
    [InlineData("top 300", 300)]
    [InlineData("TOP 10", 10)]
    [InlineData("  top   50  ", 50)]
    [InlineData("look", 0)]
    [InlineData("top", 0)]
    [InlineData("topple 5", 0)]
    public void ParseRequestedCount_ReadsTheEchoedCommand(string line, int expected)
        => Assert.Equal(expected, LeaderboardParser.ParseRequestedCount(line));

    [Fact]
    public void ParseBlock_StitchesHeaderAndRows_IntoSnapshot()
    {
        var now = new DateTimeOffset(2026, 7, 20, 20, 3, 25, TimeSpan.Zero);
        string[] block =
        {
            "Top Heroes of the Realm",
            "-=-=-=-=-=-=-=-=-=-=-=-",
            "",
            BuildHeader(),
            "=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-",
            Row(1, "Salty Exp", "Bard", "Droppin' Loads", "1529225118"),
            Row(2, "Shooting Loads", "Ranger", "what happen", "1522027759"),
            Row(3, "Thick Loads", "Cleric", "what happen", "1487816504"),
            "[HP=91/KAI=10]:",
        };

        LeaderboardSnapshot? snap = LeaderboardParser.ParseBlock(block, now, requestedCount: 100);

        Assert.NotNull(snap);
        Assert.Equal(100, snap!.RequestedCount);
        Assert.Equal(3, snap.Entries.Count);
        Assert.Equal("Salty Exp", snap.Entries[0].Name);
        Assert.Equal("Thick Loads", snap.Entries[2].Name);
        Assert.Equal(now, snap.CapturedAtUtc);
    }

    [Fact]
    public void ParseBlock_NoHeader_ReturnsNull()
        => Assert.Null(LeaderboardParser.ParseBlock(
            new[] { "just some chatter", "obvious exits: north" },
            DateTimeOffset.UtcNow, 100));
}
