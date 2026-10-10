using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Follower-side ComebackRequester: one @comeback to the leader when the party walks
// off without us, and none for an uninvite, a `leave`, a move of our own, a death or
// a party teleport (GAME_MECHANICS "@comeback (follower → leader)").
public sealed class ComebackRequesterTests : IDisposable
{
    private readonly string _root;

    public ComebackRequesterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-comebackreq-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 has a plain exit north and an exit east that casts a spell on the walk.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/3 (Cast: pre-0, post-857)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string ItemRefusal = "You do not have the appropriate item to go that direction!";

    private sealed class Harness : IDisposable
    {
        public required MessageRouter Router { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required PartyState Party { get; init; }
        public required ComebackRequester Requester { get; init; }

        public bool MasterSwitchOn { get; set; } = true;
        public bool SelfDown { get; set; }
        public string? SendBlocked { get; set; }

        // Everything the requester (and whatever else a test binds) put on the wire.
        public List<string> Wire { get; } = new();

        private DateTimeOffset _clock = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _clock += by;

        public DateTimeOffset Now => _clock;

        public int Sent => Requester.LastSentForTests.Count;

        public string? LastWire => Requester.LastSentForTests.Count == 0
            ? null
            : Encoding.Latin1.GetString(Requester.LastSentForTests[^1]).TrimEnd('\r');

        public void Feed(string text) =>
            Router.Dispatch(new LineExtractor.EmittedLine(
                text, new CellAttributes[text.Length], _clock, IsPromptLine: false));

        public void FollowBoss()
        {
            Party.IsInParty = true;
            Party.SelfIsLeader = false;
            Party.LeaderName = "Boss";
        }

        public void Dispose() => Requester.Dispose();
    }

    private Harness NewHarness(Func<bool>? isMovementPrevented = null, bool following = true)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        PartyState party = new();

        Harness? h = null;
        ComebackRequester requester = new(router, tracker,
            isMovementPrevented: isMovementPrevented,
            party: party,
            isAutoEnabled: () => h!.MasterSwitchOn,
            isSelfDown: () => h!.SelfDown,
            sendBlocked: () => h!.SendBlocked);
        h = new Harness
        {
            Router = router,
            Tracker = tracker,
            Party = party,
            Requester = requester,
        };
        requester.NowProvider = () => h.Now;
        requester.SetWireSender(b => h.Wire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        if (following) h.FollowBoss();
        tracker.SetLocated(new RoomKey(1, 1), h.Now);
        return h;
    }

    // ----- "no longer following" behind a failed follow move ---------------

    [Fact]
    public void StuckThenNoLongerFollowing_SendsComeback()
    {
        using Harness h = NewHarness();

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Boss.");

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    // Stock's line for a stunned follower, printed before the same "no longer
    // following" line as the held one.
    [Fact]
    public void StunnedThenNoLongerFollowing_SendsComeback()
    {
        using Harness h = NewHarness();

        h.Feed("You are too stunned to move anywhere!");
        h.Feed("You are no longer following Boss.");

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    [Fact]
    public void HeavyThenNoLongerFollowing_SendsComeback()
    {
        using Harness h = NewHarness();

        h.Feed("You are too heavy to move!");
        h.Feed("You are no longer following Boss.");

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    [Fact]
    public void RoomNotConfirmed_SendsBareComeback()
    {
        using Harness h = NewHarness();
        // A move in flight: the room is a prediction, not a place to send the leader.
        h.Tracker.NoteMoveSent(Direction.N, h.Now);
        h.Advance(TimeSpan.FromSeconds(30));

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Boss.");

        Assert.Equal("/Boss @comeback", h.LastWire);
    }

    // A knockdown answers with "You are flat on your back!", which is none of the
    // refusal lines, so the hold itself is the tell (report paradigm-20260922-085609).
    [Fact]
    public void KnockedDownThenNoLongerFollowing_SendsComeback()
    {
        using Harness h = NewHarness(isMovementPrevented: () => true);

        h.Feed("You are no longer following Boss.");

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    [Fact]
    public void ExitRefusalThenNoLongerFollowing_SendsComeback()
    {
        using Harness h = NewHarness();

        h.Feed("There is a closed door in that direction!");
        h.Feed("You are no longer following Boss.");

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
        Assert.Contains("closed door", h.Requester.LastIncidentSummary);
    }

    // The monsters' parting attack held us mid-move: the follow ends with no
    // refusal line, right behind the leader's own departure.
    [Fact]
    public void LeaderLeftThenNoLongerFollowing_SendsComeback()
    {
        using Harness h = NewHarness();

        h.Feed("Boss just left to the north.");
        h.Feed("You are no longer following Boss.");

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    // ----- a refusal nobody asked for (the follow ends without a line) -----

    [Fact]
    public void UnpromptedExitRefusal_SendsOnceSettled()
    {
        using Harness h = NewHarness();

        h.Feed(ItemRefusal);
        Assert.Null(h.LastWire);
        Assert.Equal("Boss", h.Requester.PendingCheckFor);

        h.Requester.FireSettleForTests();

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
        Assert.Contains("appropriate item", h.Requester.LastIncidentSummary);
        Assert.Null(h.Requester.PendingCheckFor);
    }

    [Theory]
    [InlineData("There is a closed door in that direction!")]
    [InlineData("The door is closed!")]
    [InlineData("The gate is Closed!")]
    [InlineData("You do not have enough to cover the toll of 50 gold crowns.")]
    [InlineData("You have not progressed far enough to go through this exit!")]
    [InlineData("You may not go through this exit!")]
    [InlineData("You are too good to go through this exit!")]
    [InlineData("You are not permitted in that room!")]
    public void EveryMoveOnlyRefusal_CountsAsARefusedFollowMove(string line)
    {
        using Harness h = NewHarness();

        h.Feed(line);
        h.Requester.FireSettleForTests();

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
    }

    // `look <dir>` at a shut door answers with this; no move was refused.
    [Fact]
    public void ClosedDoorLookReply_IsNotARefusedMove()
    {
        using Harness h = NewHarness();

        h.Feed("The door is closed in that direction!");

        Assert.Null(h.Requester.PendingCheckFor);
        h.Requester.FireSettleForTests();
        Assert.Null(h.LastWire);
    }

    [Fact]
    public void RefusalAnsweringOurOwnMove_IsNotAFollowMove()
    {
        using Harness h = NewHarness();

        h.Requester.NoteOwnMoveSent();
        h.Advance(TimeSpan.FromSeconds(1));
        h.Feed(ItemRefusal);

        Assert.Null(h.Requester.PendingCheckFor);
        h.Requester.FireSettleForTests();
        Assert.Null(h.LastWire);
    }

    // A typed room command can be refused in an exit's words.
    [Fact]
    public void RefusalRightAfterATypedCommand_IsNotAFollowMove()
    {
        using Harness h = NewHarness();

        h.Requester.ObserveOutbound(Encoding.Latin1.GetBytes("touch altar\r"));
        h.Feed("A strange power holds you back!");

        Assert.Null(h.Requester.PendingCheckFor);
    }

    // ----- the leader seen leaving -------------------------------------------

    [Fact]
    public void LeaderLeft_NoFollowMove_SendsOnceSettled()
    {
        using Harness h = NewHarness();

        h.Feed("Boss just left to the north.");
        Assert.Null(h.LastWire);

        h.Requester.FireSettleForTests();

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
        Assert.Contains("Boss left north", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void LeaderLeft_ThenFollowLine_SendsNothing()
    {
        using Harness h = NewHarness();

        h.Feed("Boss just left to the north.");
        h.Feed(" -- Following your Party leader north --");

        Assert.Null(h.Requester.PendingCheckFor);
        h.Requester.FireSettleForTests();
        Assert.Null(h.LastWire);
    }

    // An exit that prints no follow line (an item exit crossed with the item in
    // hand): the new room is the proof we went along.
    [Fact]
    public void LeaderLeft_AndWeStandElsewhere_SendsNothing()
    {
        using Harness h = NewHarness();

        h.Feed("Boss just left to the north.");
        h.Tracker.SetLocated(new RoomKey(1, 2), h.Now);
        h.Requester.FireSettleForTests();

        Assert.Null(h.LastWire);
    }

    [Fact]
    public void SomeoneElseLeaving_IsNotTheLeader()
    {
        using Harness h = NewHarness();

        h.Feed("Stranger just left to the north.");
        h.Feed("The orc rogue just left to the north.");

        Assert.Null(h.Requester.PendingCheckFor);
    }

    // ----- our own `par` -----------------------------------------------------

    // On the list, following nobody: how the game leaves a follower whose follow
    // move it refused without a word.
    [Fact]
    public void ParListingTheLeaderAsInvited_SendsComeback()
    {
        using Harness h = NewHarness(following: false);
        using PartyManager manager = new(h.Router, h.Party);
        manager.LeaderListedAsInvited += h.Requester.NoteLeaderListedAsInvited;
        h.Feed("You are now following Boss.");

        h.Feed("The following people are in your travel party:");
        manager.FeedTestLines(new[] { "  Boss Hogg                      (Warrior)    [Invited]" });

        Assert.Equal("/Boss @comeback 1/1", h.LastWire);
        Assert.Contains("[Invited]", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void ParListingSomeoneElseAsInvited_SendsNothing()
    {
        using Harness h = NewHarness(following: false);
        using PartyManager manager = new(h.Router, h.Party);
        manager.LeaderListedAsInvited += h.Requester.NoteLeaderListedAsInvited;
        h.Feed("You are now following Boss.");

        h.Feed("The following people are in your travel party:");
        manager.FeedTestLines(new[] { "  Stranger Danger                (Mage)       [Invited]" });

        Assert.Null(h.LastWire);
    }

    // ----- never a left-behind -------------------------------------------------

    // An uninvite, a disband, the leader teleported: the same line, with no follow
    // move of ours refused before it.
    [Fact]
    public void NoLongerFollowing_WithNothingBeforeIt_StaysSilent()
    {
        using Harness h = NewHarness();

        h.Feed("You are no longer following Boss.");

        Assert.Null(h.LastWire);
        Assert.Contains("uninvited", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void FailureTooLongAgo_StaysSilent()
    {
        using Harness h = NewHarness();

        h.Feed("You can't seem to move anywhere!");
        h.Advance(TimeSpan.FromSeconds(10));     // past the 3 s window
        h.Feed("You are no longer following Boss.");

        Assert.Null(h.LastWire);
    }

    [Fact]
    public void QuotedHeavyChatLine_DoesNotArm()
    {
        using Harness h = NewHarness();

        // Player chat is quoted, and the heavy pattern won't read past a quote.
        h.Feed("Stranger gossips \"my pack is too heavy to move lol\".");
        h.Feed("You are no longer following Boss.");

        Assert.Null(h.LastWire);
    }

    [Theory]
    [InlineData("leave")]
    [InlineData("leave party")]
    [InlineData("follow")]
    [InlineData("join Stranger")]
    public void TypedPartyCommand_IsTheUserLeaving(string typed)
    {
        // Held as well, which without the command would be a left-behind.
        using Harness h = NewHarness(isMovementPrevented: () => true);

        h.Requester.ObserveOutbound(Encoding.Latin1.GetBytes(typed + "\r"));
        h.Advance(TimeSpan.FromSeconds(1));
        h.Feed("You are no longer following Boss.");

        Assert.Null(h.LastWire);
        Assert.Contains("left the party by command", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void MoveOfOurOwn_IsTheUserLeaving()
    {
        using Harness h = NewHarness();

        h.Tracker.NoteMoveSentByObserver(Direction.S, h.Now);   // typed, no engine claim
        h.Advance(TimeSpan.FromSeconds(1));
        h.Feed("Boss just left to the north.");
        h.Requester.FireSettleForTests();

        Assert.Null(h.LastWire);
        Assert.Contains("a move of your own", h.Requester.LastIncidentSummary);
    }

    // A typed move the game turned away moved nobody: the party leaving right
    // after it still left us behind.
    [Fact]
    public void BonkedMoveOfOurOwn_DoesNotExcuseTheLeader()
    {
        using Harness h = NewHarness();

        h.Tracker.NoteMoveSentByObserver(Direction.S, h.Now);
        h.Feed("There is no exit in that direction!");
        h.Advance(TimeSpan.FromSeconds(1));
        h.Feed("Boss just left to the north.");
        h.Requester.FireSettleForTests();

        Assert.StartsWith("/Boss @comeback", h.LastWire);
    }

    [Fact]
    public void RelayedPartyTeleport_IsTheLeadersSplit()
    {
        using Harness h = NewHarness();

        h.Requester.NotePartyRelay();
        h.Advance(TimeSpan.FromSeconds(6));      // a teleport that waits before it moves anyone
        h.Feed("A strange power holds you back!");
        h.Requester.FireSettleForTests();

        Assert.Null(h.LastWire);
        Assert.Contains("party teleport", h.Requester.LastIncidentSummary);
    }

    // The leader stepped through an exit that casts on the walk and the follow
    // ended: the spell teleported them, which drops every follower.
    [Fact]
    public void LeaderLeftThroughACastingExit_IsATeleportSplit()
    {
        using Harness h = NewHarness();

        h.Feed("Boss just left to the east.");
        h.Feed("You are no longer following Boss.");

        Assert.Null(h.LastWire);
        Assert.Contains("casts a spell", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void OurOwnDeath_IsNotALeftBehind()
    {
        using Harness h = NewHarness();
        h.SelfDown = true;

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Boss.");

        Assert.Null(h.LastWire);
        Assert.Contains("we died or dropped", h.Requester.LastIncidentSummary);
    }

    [Theory]
    [InlineData("Boss is dead.")]
    [InlineData("Boss drops to the ground!")]
    public void LeadersDeath_IsNotALeftBehind(string line)
    {
        using Harness h = NewHarness();

        h.Feed(line);
        h.Feed(ItemRefusal);
        h.Requester.FireSettleForTests();

        Assert.Null(h.LastWire);
        Assert.Contains("Boss died or dropped", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void SettingOff_DetectsButDoesNotSend()
    {
        using Harness h = NewHarness();
        h.Requester.Enabled = false;

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Boss.");

        Assert.Null(h.LastWire);
        Assert.Contains("is off", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void MasterSwitchOff_SendsNothing()
    {
        using Harness h = NewHarness();
        h.MasterSwitchOn = false;

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Boss.");
        h.Feed("You are now following Boss.");
        h.Feed(ItemRefusal);
        h.Requester.FireSettleForTests();

        Assert.Null(h.LastWire);
        Assert.Contains("master switch", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void SendGateHeld_SendsNothing()
    {
        using Harness h = NewHarness();
        h.SendBlocked = "we are at the board's menu";

        h.Feed(ItemRefusal);
        h.Requester.FireSettleForTests();

        Assert.Null(h.LastWire);
        Assert.Contains("board's menu", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void Solo_SendsNothing()
    {
        using Harness h = NewHarness(following: false);

        h.Feed(ItemRefusal);
        h.Feed("Boss just left to the north.");

        Assert.Null(h.Requester.PendingCheckFor);
        h.Requester.FireSettleForTests();
        Assert.Null(h.LastWire);
    }

    [Fact]
    public void AsLeader_SendsNothing()
    {
        using Harness h = NewHarness(following: false);
        h.Party.IsInParty = true;
        h.Party.SelfIsLeader = true;
        h.Party.LeaderName = "Self";

        h.Feed(ItemRefusal);
        h.Feed("Stranger just left to the north.");
        h.Feed("Self just left to the north.");

        Assert.Null(h.Requester.PendingCheckFor);
        h.Requester.FireSettleForTests();
        Assert.Null(h.LastWire);
    }

    // The party dissolving under a pending check (our own `par` says we're in no
    // party) means we weren't a follower left standing.
    [Fact]
    public void PartyGoneBeforeTheSettle_SendsNothing()
    {
        using Harness h = NewHarness();

        h.Feed("Boss just left to the north.");
        h.Party.IsInParty = false;
        h.Requester.FireSettleForTests();

        Assert.Null(h.LastWire);
    }

    // ----- one request per incident --------------------------------------------

    [Fact]
    public void OneRequestPerIncident_UntilFollowingAgain()
    {
        using Harness h = NewHarness();

        h.Feed(ItemRefusal);
        h.Requester.FireSettleForTests();
        Assert.Equal(1, h.Sent);

        // Still standing there: more of the same evidence asks nothing more.
        h.Advance(TimeSpan.FromSeconds(20));
        h.Feed(ItemRefusal);
        h.Feed("Boss just left to the north.");
        h.Requester.FireSettleForTests();
        Assert.Equal(1, h.Sent);

        // Picked up, and left behind a second time.
        h.Feed("You are now following Boss.");
        h.Advance(TimeSpan.FromSeconds(20));
        h.Feed(ItemRefusal);
        h.Requester.FireSettleForTests();
        Assert.Equal(2, h.Sent);
    }

    // The refusal and the leader's departure are one split, however they arrive.
    [Fact]
    public void RefusalAndDepartureTogether_AreOneRequest()
    {
        using Harness h = NewHarness();

        h.Feed("Boss just left to the north.");
        h.Feed(ItemRefusal);
        h.Requester.FireSettleForTests();
        h.Requester.FireSettleForTests();

        Assert.Equal(1, h.Sent);
        Assert.Contains("Boss left north", h.Requester.LastIncidentSummary);
        Assert.Contains("appropriate item", h.Requester.LastIncidentSummary);
    }

    [Fact]
    public void FailureIsConsumed_SecondUnfollowStaysSilent()
    {
        using Harness h = NewHarness();

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Boss.");
        Assert.Equal(1, h.Sent);

        // A later, unrelated unfollow must not reuse the consumed failure.
        h.Feed("You are no longer following Stranger.");
        Assert.Equal(1, h.Sent);
    }

    // An idle leader says so; the follower logs it and asks no second time.
    [Fact]
    public void LeadersDecline_IsKept_AndNotAnswered()
    {
        using Harness h = NewHarness();
        h.Feed(ItemRefusal);
        h.Requester.FireSettleForTests();

        h.Feed("Stranger telepaths: {I can't I'm idle}");
        Assert.DoesNotContain("idle", h.Requester.LastIncidentSummary);

        h.Feed("Boss telepaths: {I can't I'm idle}");

        Assert.Contains("leader answered: I can't I'm idle", h.Requester.LastIncidentSummary);
        Assert.Equal(1, h.Sent);
    }

    // ----- held and left behind --------------------------------------------------

    // A follower too heavy to move tells the leader to wait, and then, left behind
    // anyway, asks them back: the two telepaths the Stock capture shows, in order.
    [Fact]
    public void TooHeavyAndLeftBehind_SendsWaitThenOneComeback()
    {
        using Harness h = NewHarness();
        using InventoryManager inventory = new(log: null);
        PartyRestSync rest = new(h.Party);
        rest.SetWireSender(b => h.Wire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        using TooHeavyWaitSignal heavy = new(h.Router, inventory, rest, new MovementCoordinator());
        heavy.SetWireSender(_ => { });

        h.Feed("Boss just left to the north.");
        h.Feed("You are too heavy to move!");
        h.Feed("You are no longer following Boss.");
        h.Requester.FireSettleForTests();

        Assert.Equal(new[] { "/Boss @wait (too heavy to move)", "/Boss @comeback 1/1" }, h.Wire);
    }
}
