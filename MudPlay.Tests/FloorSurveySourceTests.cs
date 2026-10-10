using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A room display lists the visible floor; the reply to a room search lists only the
// hidden stacks it found. The lists themselves read alike, so the floor reader goes
// by what comes ahead of one, and names a source only on proof: a search's echo
// directly ahead of the list for a reply, a room's name ahead of it for a display. A
// list with neither is unknown. The bytes here are the shapes report
// paradigm-20261009-164508 captured: the echo on the prompt's row, then the reply
// on its own.
public sealed class FloorSurveySourceTests
{
    private const string Prompt = "[HP=611/MA=720]:";

    private readonly TerminalEmulator _emulator = new(80, 24);
    private readonly GroundItemTracker _ground;

    public FloorSurveySourceTests()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        _ground = new GroundItemTracker(router, new CurrencyNaming(),
            isRoomName: line => line == "Bronze House Room");
        LineExtractor lines = new(_emulator);
        lines.LineEmitted += router.Dispatch;
        _ground.AttachLineExtractor(lines);
    }

    private void Wire(params string[] rows)
    {
        foreach (string row in rows) _emulator.Feed(Encoding.Latin1.GetBytes(row + "\r\n"));
    }

    [Fact]
    public void AListAfterTheEchoOfASearch_IsASearchReply()
    {
        Wire(Prompt + "sea", "You notice 2 wooden skiff, 10 black diamond here.");

        Assert.Equal(FloorSurveySource.SearchReply, _ground.LastSurveySource);
        Assert.Equal(new[] { "2 wooden skiff", "10 black diamond" }, _ground.Items);
    }

    [Theory]
    [InlineData("sea")]
    [InlineData("sear")]
    [InlineData("SEARCH")]
    public void EveryShortFormOfSearch_Counts(string typed)
    {
        Wire(Prompt + typed, "You notice a black diamond here.");

        Assert.Equal(FloorSurveySource.SearchReply, _ground.LastSurveySource);
    }

    [Theory]
    [InlineData("se")]            // southeast
    [InlineData("sea n")]         // a search for a hidden exit
    [InlineData("searching")]
    [InlineData("l")]
    public void AnythingElse_IsNotARoomSearch(string typed) =>
        Assert.False(FloorListLine.IsRoomSearch(typed));

    // The display's list follows the room's name, not an echo.
    [Fact]
    public void ARoomDisplaysList_IsTheRoomDisplay()
    {
        Wire(Prompt + "n",
             "Bronze House Room",
             "You notice 34 rope and grapple, 10 pulsating heart, scorpion tail here.",
             "Obvious exits: north, east");

        Assert.Equal(FloorSurveySource.RoomDisplay, _ground.LastSurveySource);
    }

    // The capture's vault was redisplayed between two searches. The redisplay must
    // not read as one more search reply.
    [Fact]
    public void ARedisplayBetweenSearches_IsToldFromTheReplies()
    {
        Wire(Prompt + "sea", "You notice 2 wooden skiff, 8 black diamond here.");
        Assert.Equal(FloorSurveySource.SearchReply, _ground.LastSurveySource);

        Wire(Prompt, "Bronze House Room", "You notice 34 rope and grapple here.", "Obvious exits: north, east");
        Assert.Equal(FloorSurveySource.RoomDisplay, _ground.LastSurveySource);

        Wire(Prompt + "sea", "You notice 2 wooden skiff, 9 black diamond here.");
        Assert.Equal(FloorSurveySource.SearchReply, _ground.LastSurveySource);
    }

    // Stock breaks a long reply into rows itself; the rows after the first follow a
    // row of the list, and the list is still the search's.
    [Fact]
    public void AReplyTheGameBrokeOverRows_IsStillASearchReply()
    {
        Wire(Prompt + "sea",
             "You notice 2 wooden skiff, 2 log raft, scorpion tail, pulsating heart,",
             "10 black diamond here.");

        Assert.Equal(FloorSurveySource.SearchReply, _ground.LastSurveySource);
        Assert.Equal(5, _ground.Items.Count);
    }

    // A long description sits between the name and the list; the list is still the display's.
    [Fact]
    public void ADisplayWithItsDescription_IsTheRoomDisplay()
    {
        Wire(Prompt + "l",
             "Bronze House Room",
             "    A bare stone room, its walls hung with dented shields.",
             "You notice 34 rope and grapple here.",
             "Obvious exits: north, east");

        Assert.Equal(FloorSurveySource.RoomDisplay, _ground.LastSurveySource);
    }

    // The exits line ends a display. A list that comes after it, with no room name
    // of its own ahead of it, is not that display's.
    [Fact]
    public void AListAfterADisplayHasEnded_IsNotThatDisplays()
    {
        Wire(Prompt + "n", "Bronze House Room", "Obvious exits: north, east");
        Wire("You notice 2 wooden skiff here.");

        Assert.Equal(FloorSurveySource.Unknown, _ground.LastSurveySource);
    }

    // A statline the client can't split never shows it an echo, so nothing says
    // which command a list answers.
    [Fact]
    public void WithNoEchoEverRead_TheSourceIsUnknown()
    {
        Wire("<611hp 720ma>sea", "You notice 2 wooden skiff here.");

        Assert.Equal(FloorSurveySource.Unknown, _ground.LastSurveySource);
    }

    // One line of the game's between a search's echo and its reply takes the echo
    // away. That does not make the reply a room display.
    [Fact]
    public void AReplyKnockedAwayFromItsEcho_IsUnknown_NotARoomDisplay()
    {
        Wire(Prompt + "n", "Bronze House Room", "Obvious exits: north, east");
        Wire(Prompt + "sea", "A bronze guardian peers at you.", "You notice 2 mace here.");

        Assert.Equal(FloorSurveySource.Unknown, _ground.LastSurveySource);
    }

    // A statline that keeps "[HP=…]:" and puts text of its own after it: what
    // follows the prompt is that text with the command on its end, never a bare
    // search. Its replies are unknown for the whole session, not room displays.
    [Fact]
    public void AStatlineWithTextAfterThePrompt_LeavesRepliesUnknown()
    {
        const string statline = "[HP=611/MA=720]: Exp=1234 >";
        Wire(statline + "n", "Bronze House Room", "Obvious exits: north, east");
        Wire(statline + "sea", "You notice 2 mace here.");

        Assert.Equal(FloorSurveySource.Unknown, _ground.LastSurveySource);
    }
}
