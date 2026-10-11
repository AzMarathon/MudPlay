using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The report-only chat censor. The words here are made-up stand-ins added to an
// empty table; no real word from the shipped table appears in a test.
public sealed class BugReportChatCensorTests
{
    private static readonly BugReportChatCensor Censor =
        new BugReportChatCensor(Array.Empty<ulong>()).WithWords("flibber", "zorptang");

    [Fact]
    public void Normalise_UndoesSubstitutionsPunctuationAndStretching()
    {
        Assert.Equal("flibber", BugReportChatCensor.Normalise("Fl1bb3r", collapseAll: false));
        Assert.Equal("flibber", BugReportChatCensor.Normalise("f.l-i_b*b e r", collapseAll: false));
        Assert.Equal("flibber", BugReportChatCensor.Normalise("flibbbbber", collapseAll: false));
        Assert.Equal("fliber", BugReportChatCensor.Normalise("flibbbber", collapseAll: true));
        Assert.Equal("as", BugReportChatCensor.Normalise("@$$", collapseAll: true));
    }

    [Fact]
    public void Hash_MatchesTheValuesTheTableWasGeneratedWith()
    {
        // Pinned against the generator, so a change to the salt or the hashing
        // can't silently empty the shipped table.
        Assert.Equal(0x054AC24ECD77F705UL, BugReportChatCensor.Hash("flibber", collapsedAll: false));
        Assert.Equal(0x70B5073F83F55115UL, BugReportChatCensor.Hash("fliber", collapsedAll: true));
        Assert.Equal(0xD4ED537F83FE2D9CUL, BugReportChatCensor.Hash("zorptang", collapsedAll: false));
    }

    [Theory]
    [InlineData("flibber")]
    [InlineData("FLIBBER")]
    [InlineData("fl1bb3r")]
    [InlineData("fliiiibber")]
    [InlineData("zorptang")]
    public void IsListed_TakesSpellingsOfAListedWord(string token) => Assert.True(Censor.IsListed(token));

    [Theory]
    [InlineData("flibberty")]      // contains a listed word: not a substring match
    [InlineData("unflibber")]
    [InlineData("fliber")]         // one b: a different word unless it was stretched
    [InlineData("zorp")]
    [InlineData("sword")]
    public void IsListed_LeavesOtherWords(string token) => Assert.False(Censor.IsListed(token));

    [Fact]
    public void CensorIfChat_MasksChatAndKeepsPunctuation()
    {
        Assert.Equal("Somebody gossips: you [censored]!",
            Censor.CensorIfChat("Somebody gossips: you flibber!"));
        Assert.Equal("Somebody telepaths: [censored], [censored]-head.",
            Censor.CensorIfChat("Somebody telepaths: Zorptang, flibber-head."));
        Assert.Equal("You say \"[censored]\"", Censor.CensorIfChat("You say \"FL1BB3R\""));
    }

    [Fact]
    public void CensorIfChat_NeverTouchesGameText()
    {
        // The same word in a room, item or combat line is the game's own.
        const string room = "You notice a flibber sword here.";
        const string combat = "The zorptang swings at you with a flibber!";
        Assert.Equal(room, Censor.CensorIfChat(room));
        Assert.Equal(combat, Censor.CensorIfChat(combat));
    }

    [Fact]
    public void CensorRows_FollowsAWrappedChatLineOntoItsNextRow()
    {
        string first = "Somebody gossips: " + new string('x', 61);   // 79 of 80 columns
        List<string> rows =
        [
            "You notice a flibber here.",
            first,
            "and then flibber to finish",
            "Obvious exits: flibber",
        ];

        Censor.CensorRows(rows, terminalColumns: 80);

        Assert.Equal("You notice a flibber here.", rows[0]);
        Assert.Equal(first, rows[1]);
        Assert.Equal("and then [censored] to finish", rows[2]);
        Assert.Equal("Obvious exits: flibber", rows[3]);
    }

    [Theory]
    [InlineData("gos flibber", "gos [censored]")]
    [InlineData("/Somebody flibber you", "/Somebody [censored] you")]
    [InlineData(".flibber", ".[censored]")]
    [InlineData("say zorptang", "say [censored]")]
    [InlineData("bg flibber", "bg [censored]")]
    [InlineData("get flibber", "get flibber")]        // not a chat command
    [InlineData("a zorptang", "a zorptang")]
    public void CensorSentCommand_MasksOnlyChannelCommands(string command, string expected) =>
        Assert.Equal(expected, Censor.CensorSentCommand(command));

    [Fact]
    public void CensorRawWireLine_MasksBetweenColourCodes()
    {
        Assert.Equal("^[[0;35mSomebody gossips: ^[[1mso ^[[0m[censored]^M",
            Censor.CensorRawWireLine("^[[0;35mSomebody gossips: ^[[1mso ^[[0mflibber^M"));
        const string game = "^[[0;32mYou notice a flibber here.^M";
        Assert.Equal(game, Censor.CensorRawWireLine(game));
    }

    [Fact]
    public void CensorLogMessage_MasksOnlyAQuotedChatLineOrChatCommand()
    {
        Assert.Equal("'greet' matched: Somebody gossips: [censored]",
            Censor.CensorLogMessage("'greet' matched: Somebody gossips: flibber"));
        Assert.Equal("sent `gos [censored]`", Censor.CensorLogMessage("sent `gos flibber`"));
        const string own = "picked up a flibber: 2 held";
        Assert.Equal(own, Censor.CensorLogMessage(own));
    }

    [Fact]
    public void ShippedTable_IsNotEmptyAndLeavesOrdinaryWordsAlone()
    {
        BugReportChatCensor shipped = BugReportChatCensor.Default;
        const string line = "Somebody gossips: selling a bastard sword and a silver ring, assassin class only";
        Assert.Equal(line, shipped.CensorIfChat(line));
        Assert.True(BugReportCensorTable.Hashes.Length > 500);
    }
}
