using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// An exit's own message is printed in a social's green and shape, so it used to fill
// the conversation. Each case is one server read written as the Stock engine writes it
// (GAME_MECHANICS "Exit messages and socials on the wire"): the clear-line, the
// colour, the line, then the room display (its bright-cyan title through its
// "Obvious exits:" line), or the one line printed when the room can't be seen. The
// exit texts are the Stock map and message tables' own for the reported rooms.
public sealed class ExitTextConversationTests
{
    private const string Prompt = "[HP=100/MA=50]:";
    private const string Clear = "\u001b[79D\u001b[K";
    private const string Green = "\u001b[0;32m";
    private const string Yellow = "\u001b[1;33m";
    private const string Cyan = "\u001b[1;36m";
    private const string White = "\u001b[0;37m";

    // The router, chat router, party and trigger engine wired as the client wires them:
    // the router takes each line ahead of the trigger engine, and ChatRouter is told
    // who we follow by the party state PartyManager keeps.
    private sealed class Wire
    {
        public readonly MessageRouter Router = new();
        public readonly ChatRouter Chat;
        public readonly PartyState Party = new();
        public readonly PartyManager PartyManager;
        public readonly ServerFeed Feed;
        public readonly TriggerEngine Triggers;
        public readonly List<ChatLogEntry> Entries = new();
        public readonly List<string> Lines = new();
        public readonly List<string> Sent = new();

        public Wire()
        {
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router)
            {
                IsKnownPlayer = name => name is "Raijin" or "Bob",
                IsLeaderWeFollow = Party.IsLeaderWeFollow,
            };
            Chat.EntryClassified += Entries.Add;
            PartyManager = new PartyManager(Router, Party);
            Feed = new ServerFeed(Router);
            Feed.Lines.LineEmitted += line => Lines.Add(line.Text);
            Triggers = new TriggerEngine(TestProfiles.Loaded(), Chat, new LogService(), onUiThread: act => act());
            Triggers.AttachLineExtractor(Feed.Lines);
            Triggers.SetSender(bytes => Sent.Add(System.Text.Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        }

        public IEnumerable<ChatLogEntry> Socials => Entries.Where(e => e.Channel == ChatChannel.Social);

        // One game-message trigger and one chat trigger on the same words, each
        // answering with its scope's name.
        public void TriggersOn(string text)
        {
            Triggers.Add(new Trigger(Name: "game", Enabled: true, Scope: TriggerScope.GameMessages,
                MatchType: TriggerMatchType.Literal, Pattern: text, Response: "game"));
            Triggers.Add(new Trigger(Name: "chat", Enabled: true, Scope: TriggerScope.ChatAny,
                MatchType: TriggerMatchType.Literal, Pattern: text, Response: "chat"));
        }
    }

    // The room display as _display_room_desc prints it, then the prompt it ends on.
    private static string Display(string name, string exits) =>
        $"{Clear}{Cyan}{name}\r\n{White}A worn stretch of ground.\r\n{Green}Obvious exits: {exits}\r\n{White}{Prompt}";

    private static string Dark(string band) =>
        $"{Clear}{White}The room is {band} - you can't see anything\r\n{Prompt}";

    // The move typed at the prompt, its echo and the line feed that sends it.
    private static string Typed(string command) => $"{Prompt}{command}\r\n";

    public static TheoryData<string, string, string, string> ReportedMoves => new()
    {
        // 1/2335 Small Pier, north: a type-5 exit, message 617.
        { "n", Green, "You hop in one of the skiffs, and row to Newhaven.", Display("Newhaven, Docks", "north, south") },
        // 1/2149 Newhaven, Docks, `borrow skiff`: the Text exit south, message 8563, printed
        // bright yellow (_move_user @0x4183b6), which no social is. The user recalls only the
        // green north-skiff line getting in.
        { "borrow skiff", Yellow, "You climb into one of the skiffs, and row to Silvermere.", Display("Small Pier", "north, south") },
        // 1/1764 Huge Cave, northwest into the pit: type 5, message 8341.
        { "nw", Green, "You step onto the crumbling edge of a pit, and fall inside!", Display("Huge Cave, Refuse Pit", "southeast") },
        // 1/1765 the pit, southeast out of it: type 5, message 8342.
        { "se", Green, "You scramble out of the shallow pit.", Display("Huge Cave", "south, northwest") },
        // Both cave rooms sit at light -175: without a light the display is the one line.
        { "nw", Green, "You step onto the crumbling edge of a pit, and fall inside!", Dark("very dark") },
        { "se", Green, "You scramble out of the shallow pit.", Dark("pitch black") },
    };

    [Theory]
    [MemberData(nameof(ReportedMoves))]
    public void OwnExitText_ShownInTheTerminal_NeverReachesTheConversation(
        string command, string colour, string exitText, string display)
    {
        Wire w = new();

        w.Feed.Raw(Typed(command) + $"{Clear}{colour}{exitText}\r\n" + display);

        Assert.Empty(w.Socials);
        Assert.Contains(exitText, w.Lines);
        Assert.Equal(colour == Green ? 1 : 0, w.Chat.MoveLinesKeptOut);
    }

    // A follower's read: the leader's departure line (to the room), its prompt redrawn,
    // then the follower's own exit line wiping that prompt, and the room display.
    private static string FollowersRead(string leaderLine) =>
        $"{Clear}{Yellow}{Green}{leaderLine}\r\n{White}{Prompt}"
        + $"{Clear}{Green}You hop in one of the skiffs, and row to Newhaven.\r\n"
        + Display("Newhaven, Docks", "north, south");

    [Fact]
    public void FollowersRead_LeadersDepartureAndOwnExitText_NeitherReachesTheConversation()
    {
        Wire w = new();
        w.Feed.Line("You are now following Raijin.");

        w.Feed.Raw(FollowersRead("Raijin hops into one of the skiffs, and rows away."));

        Assert.Empty(w.Socials);
        Assert.Contains("Raijin hops into one of the skiffs, and rows away.", w.Lines);
        Assert.Equal(2, w.Chat.MoveLinesKeptOut);
    }

    // Only the leader we follow is moved with us: anyone else's line in that read is theirs.
    [Fact]
    public void OwnExitText_AnotherPlayersSocialInTheSameRead_StillReachesTheConversation()
    {
        Wire w = new();
        w.Feed.Line("You are now following Raijin.");

        w.Feed.Raw(FollowersRead("Bob waves!"));

        Assert.Equal("Bob waves!", Assert.Single(w.Socials).Message);
        Assert.Equal(1, w.Chat.MoveLinesKeptOut);
    }

    [Fact]
    public void NotFollowing_ThePlayersLineAheadOfOurExitText_StillReachesTheConversation()
    {
        Wire w = new();

        w.Feed.Raw(FollowersRead("Raijin hops into one of the skiffs, and rows away."));

        Assert.Equal("Raijin hops into one of the skiffs, and rows away.", Assert.Single(w.Socials).Message);
    }

    // A title-coloured line is no room display without the display's exits line:
    // Stock prints a resisted spell in the title's bright cyan.
    [Fact]
    public void OwnSocial_FollowedByABrightCyanResistLine_ReachesTheConversation()
    {
        Wire w = new();

        w.Feed.Raw(Typed("laugh") + $"{Green}You laugh!\r\n{White}{Prompt}"
            + $"{Clear}{Cyan}The orc resists your spell!\r\n{White}{Prompt}");

        Assert.Equal("You laugh!", Assert.Single(w.Socials).Message);
        Assert.Equal(0, w.Chat.MoveLinesKeptOut);
    }

    // Held for its read, a real social fires the chat triggers only, as it always did;
    // an exit line kept out fires the game-message triggers, as any game line does.
    [Fact]
    public void RealSocial_FiresChatTriggersOnly()
    {
        Wire w = new();
        w.TriggersOn("Raijin waves!");

        w.Feed.Raw($"{Clear}{Green}Raijin waves!\r\n{White}{Prompt}");

        Assert.Equal(new[] { "chat" }, w.Sent);
    }

    [Fact]
    public void KeptOutExitText_FiresGameMessageTriggersOnly()
    {
        Wire w = new();
        w.TriggersOn("You hop in one of the skiffs, and row to Newhaven.");

        w.Feed.Raw(Typed("n") + $"{Clear}{Green}You hop in one of the skiffs, and row to Newhaven.\r\n"
            + Display("Newhaven, Docks", "north, south"));

        Assert.Equal(new[] { "game" }, w.Sent);
    }

    // An own social is followed by its prompt (_handle_ljn_actions @0x46071b): the prompt
    // row is left open, so the social is settled by the end of the read.
    [Theory]
    [InlineData("You laugh!")]
    [InlineData("You leap in the air!")]
    [InlineData("You hop around the room excitedly.")]
    [InlineData("You wave to Raijin!")]
    public void OwnSocial_FollowedByItsPrompt_ReachesTheConversation(string social)
    {
        Wire w = new();

        w.Feed.Raw(Typed("act") + $"{Green}{social}\r\n{White}{Prompt}");

        ChatLogEntry e = Assert.Single(w.Socials);
        Assert.Equal(social, e.Message);
        Assert.Equal(0, w.Chat.MoveLinesKeptOut);
    }

    // The prompt completed in the same read by the next command's echo.
    [Fact]
    public void OwnSocial_PromptCompletedInTheSameRead_ReachesTheConversation()
    {
        Wire w = new();

        w.Feed.Raw(Typed("laugh") + $"{Green}You laugh!\r\n" + Typed("n")
            + Display("Newhaven, Docks", "north, south"));

        Assert.Equal("You laugh!", Assert.Single(w.Socials).Message);
    }

    // Another room event wiping the social's prompt in the same read is no room display.
    [Fact]
    public void OwnSocial_PromptWipedByARoomEvent_ReachesTheConversation()
    {
        Wire w = new();

        w.Feed.Raw(Typed("laugh") + $"{Green}You laugh!\r\n{White}{Prompt}"
            + $"{Clear}{Yellow}A large rat creeps into the room from the north.\r\n{White}{Prompt}");

        Assert.Equal("You laugh!", Assert.Single(w.Socials).Message);
    }

    [Fact]
    public void OthersSocial_ReachesTheConversation_EvenWithARoomDisplayAfterIt()
    {
        Wire w = new();

        w.Feed.Raw($"{Clear}{Green}Raijin waves!\r\n{White}{Prompt}");
        w.Feed.Raw($"{Clear}{Green}Raijin grins evilly.\r\n" + Display("Small Pier", "north, south"));

        Assert.Equal(new[] { "Raijin waves!", "Raijin grins evilly." }, w.Socials.Select(e => e.Message));
    }

    // Real chat of the same words stays chat, and a social held for its read keeps its
    // place ahead of chat that came after it.
    [Fact]
    public void ChatOfTheSameShape_StaysChat_AndAHeldSocialKeepsItsPlace()
    {
        Wire w = new();

        w.Feed.Raw(Typed("laugh") + $"{Green}You laugh!\r\n{White}{Prompt}"
            + $"{Clear}{White}Raijin gossips: You hop in one of the skiffs, and row to Newhaven.\r\n{Prompt}");
        w.Feed.Raw($"{Clear}{Green}Raijin says \"You scramble out of the shallow pit.\"\r\n{White}{Prompt}");

        Assert.Equal(new[] { ChatChannel.Social, ChatChannel.Gossip, ChatChannel.Local },
            w.Entries.Select(e => e.Channel));
        Assert.Equal("You hop in one of the skiffs, and row to Newhaven.", w.Entries[1].Message);
        Assert.Equal("You scramble out of the shallow pit.", w.Entries[2].Message);
    }
}
