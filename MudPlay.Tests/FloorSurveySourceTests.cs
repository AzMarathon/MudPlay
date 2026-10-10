using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A room display lists the visible floor; the reply to a room search lists only the
// hidden stacks it found. The floor reader tells the two apart by what the list's
// first row follows, since the lists themselves read alike. The bytes here are the
// shapes report paradigm-20261009-164508 captured: the echo on the prompt's row,
// then the reply on its own.
public sealed class FloorSurveySourceTests
{
    private const string Prompt = "[HP=611/MA=720]:";

    private readonly TerminalEmulator _emulator = new(80, 24);
    private readonly GroundItemTracker _ground;

    public FloorSurveySourceTests()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        _ground = new GroundItemTracker(router, new CurrencyNaming());
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

    // A statline the client can't split never shows it an echo, so nothing says
    // which command a list answers.
    [Fact]
    public void WithNoEchoEverRead_TheSourceIsUnknown()
    {
        Wire("<611hp 720ma>sea", "You notice 2 wooden skiff here.");

        Assert.Equal(FloorSurveySource.Unknown, _ground.LastSurveySource);
    }
}
