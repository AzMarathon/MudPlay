using System.Text;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Position recovery while the map is unsure of the room, driven over the wire:
// the passive grid re-localiser folding each display with the move it answers, the
// recovery gate's tier-2 forward footprint, and the cap on the tier-3 walk-back.
// Each move goes out through a wire sender and comes back as game text through
// LineExtractor (TrackerWireRig).
public sealed class RoomTrackingRecoveryWireTests
{
    // ----- the pair world -------------------------------------------------

    // One walk, every room of it named "Hall". A room's exits are its way in and
    // its way on, and the walk turns at every step, so no two rooms of one copy
    // read alike. The walk is laid out twice, A and B, so every display fits
    // exactly its two copies and only a footprint can tell them apart: B's step
    // `split` leads to a Cellar instead, and B's room past it (reached only from
    // the Cellar) reads exactly like A's.
    private static readonly Direction[] Walk =
        { Direction.E, Direction.N, Direction.NE, Direction.SE, Direction.U, Direction.E, Direction.S };

    private static RoomKey A(int i) => new(1, 100 + i);
    private static RoomKey B(int i) => new(1, 200 + i);
    private static readonly RoomKey Cellar = new(1, 300);

    private static string PairWorld(int split, int textExitAt = -1, int decoyAt = -1)
    {
        List<string> rooms = new();
        foreach (bool copyA in new[] { true, false })
        {
            for (int i = 0; i <= Walk.Length; i++)
            {
                RoomKey me = copyA ? A(i) : B(i);
                List<(string, string)> exits = new();
                if (i > 0)
                    exits.Add((Walk[i - 1].Opposite().ToString(), Ref(copyA ? A(i - 1) : B(i - 1))));
                if (i < Walk.Length)
                {
                    RoomKey on = !copyA && i == split ? Cellar : copyA ? A(i + 1) : B(i + 1);
                    exits.Add((Walk[i].ToString(), Ref(on) + (i == textExitAt ? " (Text: go path)" : "")));
                }
                // A room B's copy has and A's lacks: shown, it fits B and the decoy.
                if (!copyA && i == decoyAt) exits.Add(("D", Ref(Cellar)));
                rooms.Add(TrackerWireRig.Room(me.Map, me.Room, "Hall", exits.ToArray()));
            }
        }
        rooms.Add(TrackerWireRig.Room(Cellar.Map, Cellar.Room, "Cellar",
            ("N", Ref(B(split))), ("S", Ref(B(split + 1)))));
        if (decoyAt >= 0)
        {
            // Reads as B's room at decoyAt does, so that display fits two rooms and
            // the tracker can't settle on it by name and exits.
            List<(string, string)> exits = new();
            if (decoyAt > 0) exits.Add((Walk[decoyAt - 1].Opposite().ToString(), Ref(Cellar)));
            if (decoyAt < Walk.Length) exits.Add((Walk[decoyAt].ToString(), Ref(Cellar)));
            exits.Add(("D", Ref(Cellar)));
            rooms.Add(TrackerWireRig.Room(1, 400, "Hall", exits.ToArray()));
        }
        return TrackerWireRig.Rooms(rooms);
    }

    private static string Ref(RoomKey key) => $"{key.Map}/{key.Room}";

    private static string Token(int step) => Walk[step].ToToken();

    // A rig standing in A's first room with the map unsure of it: its display fits
    // A(0) and B(0), so the passive footprint seeds from both.
    private static TrackerWireRig StartInPair(string world)
    {
        TrackerWireRig rig = new(world) { Position = A(0) };
        rig.ShowRoom();
        Assert.Equal(RoomConfidence.Suspect, rig.Tracker.State.Confidence);
        Assert.StartsWith("active — 2 candidates", rig.Tracker.PassiveGridStatus);
        return rig;
    }

    private static void AssertLocatedAt(TrackerWireRig rig, RoomKey room)
    {
        Assert.Equal(room, rig.Position);
        Assert.Equal(RoomConfidence.Confirmed, rig.Tracker.State.Confidence);
        Assert.Equal(room, rig.Believed);
    }

    // ----- passive grid locate -------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PassiveLocate_PlainWalk_ReanchorsOneStepPastTheSplit(bool echo)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 2));
        rig.Echo = echo;

        rig.Do(Token(0));
        rig.Do(Token(1));
        Assert.NotEqual(RoomConfidence.Confirmed, rig.Tracker.State.Confidence);
        rig.Do(Token(2));

        AssertLocatedAt(rig, A(3));
        Assert.Empty(rig.Failures);
    }

    // A party leader's drag is a move like a typed one: the follow line, then the room.
    [Fact]
    public void PassiveLocate_FollowDrag_FoldsLikeATypedMove()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));

        rig.Drag(Walk[0]);
        rig.Drag(Walk[1]);

        AssertLocatedAt(rig, A(2));
    }

    // a. A text exit (`go path`) is a move: each candidate follows its own exit of
    // that command. Folding nothing left the footprint a room behind the player.
    [Fact]
    public void PassiveLocate_TextExitMove_IsFoldedThroughEachCandidatesOwnExit()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 2, textExitAt: 2));

        rig.Do(Token(0));
        rig.Do(Token(1));
        rig.Do("go path");

        AssertLocatedAt(rig, A(3));
    }

    // b. A move the game refused or dropped while the map is unsure is never
    // folded: the `look` after it is a redisplay of the room we never left, which
    // neither steps the footprint nor counts as a strike.
    [Theory]
    [InlineData("There is no exit in that direction!")]
    [InlineData("You can't go that way.")]
    [InlineData("You may not go through this exit!")]
    [InlineData("You are typing too quickly - command ignored")]
    public void PassiveLocate_RefusedMove_IsNotFoldedByTheLookAfterIt(string refusal)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 0));
        int strikes = rig.Tracker.State.SuspectStrikes;

        rig.NextAnswers.Enqueue(refusal);
        rig.Do(Token(0));
        rig.Do("l");

        Assert.Equal(A(0), rig.Position);
        Assert.Equal(strikes, rig.Tracker.State.SuspectStrikes);
        Assert.StartsWith("active — 2 candidates, depth 0", rig.Tracker.PassiveGridStatus);

        rig.Do(Token(0));
        AssertLocatedAt(rig, A(1));
    }

    // A move the game answers with nothing the tracker can read: a typed command
    // that prints only the prompt, or a line no refusal detector knows. It must be
    // retired, or every later display is read against the move before its own. With
    // an echo, the next command's echo retires it: the game echoes a command when it
    // runs it, so it was echoed and no display came before the next one. With no echo,
    // a next move after a pause longer than the bound (3.5 s at least) does.
    [Theory]
    [InlineData("enter portal", null, true)]
    [InlineData("climb rope", null, true)]
    [InlineData("enter portal", null, false)]
    [InlineData("e", "Something unseen stops you.", true)]
    [InlineData("e", "Something unseen stops you.", false)]
    public void PassiveLocate_AMoveWithNoAnswerTheTrackerReads_IsRetired(string command, string? answer, bool echo)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        rig.Echo = echo;
        if (answer is not null) rig.NextAnswers.Enqueue(answer);

        rig.Do(command);
        if (!echo) rig.Clock.Advance(TimeSpan.FromSeconds(4));   // the player waits, then walks on
        for (int step = 0; step < 4; step++) rig.Do(Token(step));

        AssertLocatedAt(rig, A(4));
    }

    // More unanswered moves than the queue holds (typed quickly, no echo, so none is
    // retired): the passive locate starts over rather than reading every later
    // display against an older move.
    [Fact]
    public void PassiveLocate_MoreUnansweredMovesThanTheQueueHolds_StartsOver()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        rig.Echo = false;

        for (int i = 0; i < 16; i++) rig.Do("enter portal");
        Assert.Equal("idle", rig.Tracker.PassiveGridStatus);

        for (int step = 0; step < 3; step++) rig.Do(Token(step));
        AssertLocatedAt(rig, A(3));
    }

    // a. A typed command the room refuses with a line that also answers ordinary
    // commands ("You do not see that here."), sent just now, is refused as a move.
    // With no echo and no pause, nothing but the refusal can retire it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PassiveLocate_ATypedCommandRefusedByTheRoom_IsNotFolded(bool echo)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        rig.Echo = echo;
        rig.NextAnswers.Enqueue("You do not see that here.");

        rig.Do("enter portal");
        for (int step = 0; step < 3; step++) rig.Do(Token(step));

        AssertLocatedAt(rig, A(3));
    }

    // A command the game didn't echo (it printed only the prompt) is not retired by
    // the next move's echo, so that move's display is read against it and the
    // footprint exhausts. The move the display really answered was echoed, so its
    // display has come: it is dropped, and the leader's drags that follow
    // (which echo nothing) fold with their own displays, not a move behind.
    [Fact]
    public void PassiveLocate_AnExhaustedFootprint_DropsAMoveAlreadyEchoed()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 2));
        rig.Echo = false;
        rig.Do("enter portal");
        rig.Echo = true;

        rig.Do(Token(0));
        rig.Drag(Walk[1]);
        rig.Drag(Walk[2]);

        AssertLocatedAt(rig, A(3));
    }

    // The same with no echo: the move the display answered went out more than a hop
    // before it, so its display has come, and it is dropped.
    [Fact]
    public void PassiveLocate_AnExhaustedFootprint_DropsAMoveOlderThanAHop()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 2));
        rig.Echo = false;
        rig.AnswerDelay = TimeSpan.FromSeconds(1.5);
        rig.Do("enter portal");   // the game prints only the prompt

        for (int step = 0; step < 3; step++) rig.Do(Token(step));

        AssertLocatedAt(rig, A(3));
    }

    // Three moves typed together, the first landing in a room that shows an exit the
    // map lacks: the footprint exhausts on it. The two moves behind it are still out
    // (not echoed yet, and sent within a hop), so they stay: the next display seeds
    // afresh and the one after folds with its own move.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PassiveLocate_AnExhaustedFootprint_KeepsTheMovesStillOut(bool echo)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 2));
        rig.Echo = echo;
        rig.ExtraShownExits[A(1)] = new[] { Direction.D };

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Type(Token(2));
        rig.Pump();

        AssertLocatedAt(rig, A(3));
    }

    // A heavy character's hops: two moves typed together, answered 2–3 s apart with no
    // echo. Neither is taken for a move with no answer, each display folds with its
    // own move, and the footprint isn't settled before the second move lands.
    [Theory]
    [InlineData(2.0)]
    [InlineData(2.5)]
    [InlineData(3.0)]
    public void PassiveLocate_SlowHops_TypedAheadMovesFoldInOrder(double hopSeconds)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        rig.Echo = false;
        rig.AnswerDelay = TimeSpan.FromSeconds(hopSeconds);

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Pump();

        AssertLocatedAt(rig, A(2));
        Assert.DoesNotContain(rig.Log.Snapshot(), e => e.Message.Contains("got no answer"));
    }

    // The same, down to one room on the first display: the second move, landing one
    // slow hop later, is waited for rather than dropped, so the room settled on is
    // the one it reached.
    [Theory]
    [InlineData(2.0)]
    [InlineData(2.4)]
    [InlineData(3.0)]
    public void PassiveLocate_SlowHops_DownToOneWaitsForTheMoveStillOut(double hopSeconds)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 0));
        rig.Echo = false;
        rig.AnswerDelay = TimeSpan.FromSeconds(hopSeconds);

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Pump();

        AssertLocatedAt(rig, A(2));
        Assert.DoesNotContain(rig.Log.Snapshot(), e => e.Message.Contains("re-anchoring without them"));
    }

    // A character whose hop the travel model puts at 3.1 s (Paradigm, heavily
    // encumbered): the bound grows with it, so moves answered 5 s apart still fold.
    [Fact]
    public void PassiveLocate_TheBoundGrowsWithTheExpectedHop()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        rig.Echo = false;
        rig.Tracker.SetExpectedHopProbe(() => TimeSpan.FromSeconds(3.1));
        rig.AnswerDelay = TimeSpan.FromSeconds(5);

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Pump();

        AssertLocatedAt(rig, A(2));
    }

    // Moves typed together on a statline that echoes: the game echoes each when it
    // runs it, just before its display, so the second move's echo always follows the
    // first move's display and retires nothing, however slow the hops.
    [Theory]
    [InlineData(0.4)]
    [InlineData(2.5)]
    public void PassiveLocate_TypedAheadMovesWithEcho_FoldInOrder(double hopSeconds)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        rig.AnswerDelay = TimeSpan.FromSeconds(hopSeconds);

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Pump();

        AssertLocatedAt(rig, A(2));
        Assert.DoesNotContain(rig.Log.Snapshot(), e => e.Message.Contains("not counted as a move"));
    }

    // c. Down to one room, waiting on a move that never got an answer: past the bound
    // (3.5 s at least), the next display re-anchors instead of being read as that move's.
    [Fact]
    public void PassiveLocate_DownToOne_AMoveWithNoAnswerDoesNotHoldTheReanchorForGood()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 0));
        rig.Echo = false;

        rig.Type(Token(0));
        rig.Type("enter portal");   // answered with only a prompt
        rig.Pump();
        Assert.NotEqual(RoomConfidence.Confirmed, rig.Tracker.State.Confidence);

        rig.Clock.Advance(TimeSpan.FromSeconds(4));
        rig.Do("l");

        AssertLocatedAt(rig, A(1));
    }

    // c. Two moves sent before either is answered: each display folds with its own
    // move, oldest first.
    [Fact]
    public void PassiveLocate_TwoMovesInFlight_FoldInTheOrderSent()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Pump();

        AssertLocatedAt(rig, A(2));
    }

    // c. The footprint can come down to one room while a later move is still out.
    // It waits for that move's display, and lands where that move went.
    [Fact]
    public void PassiveLocate_DownToOneWithAMoveStillOut_WaitsForItsDisplay()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 0));

        rig.Type(Token(0));
        rig.Type(Token(1));
        rig.Pump();

        AssertLocatedAt(rig, A(2));
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.Contains("waiting for 1 move(s) still in flight"));
    }

    // d. A move into a room that shows nothing (dark, or blind) still answers the
    // move in flight: the footprint dead-reckons it, and the next display folds
    // with the move that led to it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PassiveLocate_MoveIntoADarkRoomOrBlind_IsDeadReckoned(bool dark)
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 1));
        if (dark)
        {
            rig.DarkRooms.Add(A(1));
            rig.DarkRooms.Add(B(1));
        }
        else
            rig.NextMoveShows.Enqueue("You are blind.");

        rig.Do(Token(0));
        Assert.StartsWith("active — 2 candidates, depth 1", rig.Tracker.PassiveGridStatus);
        rig.Do(Token(1));

        AssertLocatedAt(rig, A(2));
    }

    // d. Lost, a display no room fits exactly (one of its exits isn't shown) still
    // answered a move, so it folds: the subset match keeps both copies, and the next
    // step separates them.
    [Fact]
    public void PassiveLocate_WhileLost_ADisplayNoRoomFitsExactly_StillFolds()
    {
        using TrackerWireRig rig = StartInPair(PairWorld(split: 4));
        rig.UnshownExits[A(4)] = new[] { Walk[3].Opposite() };

        for (int step = 0; step < 4; step++) rig.Do(Token(step));
        Assert.Equal(RoomConfidence.Lost, rig.Tracker.State.Confidence);
        Assert.StartsWith("active — 2 candidates, depth 4", rig.Tracker.PassiveGridStatus);

        rig.Do(Token(4));
        AssertLocatedAt(rig, A(5));
    }

    // ----- tier-2 forward footprint --------------------------------------

    // Stands in for the loop runner at a landing it didn't expect: it reports the
    // mismatch and carries on with its plan, each step put on the wire as the
    // engines do (announced to the tracker and the gate, then sent).
    private sealed class PlanEngine : IRecoverableEngine
    {
        private readonly TrackerWireRig _rig;
        public PlanEngine(TrackerWireRig rig) => _rig = rig;

        public Queue<Direction> Plan { get; } = new();
        public List<Direction> Backtracks { get; } = new();
        public List<string> Aborts { get; } = new();

        public string Name => "PlanEngine";
        public RoomKey? JourneyOrigin => null;
        public Direction? PeekNextPlannedDirection() => Plan.Count > 0 ? Plan.Peek() : null;
        public IReadOnlyList<Direction> PeekPlannedDirections(int count) => Plan.Take(count).ToList();

        // As LoopRunner does before every planned step: the gate may hold it, or take
        // over (tier 3). Returns whether the step went out.
        public bool Step()
        {
            if (!_rig.Gate.MayProceedWithPlannedStep()) return false;
            Direction dir = Plan.Dequeue();
            _rig.Tracker.NoteMoveSent(dir);
            _rig.Gate.NoteEngineStepSent(dir);
            (Wire ?? _rig.Send)(Encoding.Latin1.GetBytes(dir.ToToken() + "\r"));
            return true;
        }

        // The engine's wire, as the client wraps it (EngineSendGate); the rig's own
        // by default.
        public Action<byte[]>? Wire { get; set; }

        // A stopped engine sends nothing, as LoopRunner's backtrack doesn't once the
        // loop is stopped.
        public bool Stopped { get; set; }

        public void SendBacktrackMove(Direction direction)
        {
            if (Stopped) return;
            Backtracks.Add(direction);
            _rig.Tracker.NoteMoveSent(direction);
            (Wire ?? _rig.Send)(Encoding.Latin1.GetBytes(direction.ToToken() + "\r"));
        }

        public void PauseForRecovery(string reason) { }
        // What the gate resumed the engine at, as the engine sees it.
        public Action<RoomKey>? Resumed { get; set; }
        public void ResumeAfterRecovery(RoomKey recoveredAnchor) => Resumed?.Invoke(recoveredAnchor);
        public void AbortFromRecoveryFailure(string detail) => Aborts.Add(detail);
    }

    private static (TrackerWireRig Rig, PlanEngine Engine) StartForward(string world)
    {
        TrackerWireRig rig = new(world) { Position = A(0) };
        PlanEngine engine = new(rig);
        rig.Tracker.SetLocated(A(0));   // sure of a room two copies share
        rig.ShowRoom();
        rig.Gate.Attach(engine);
        rig.Gate.NoteSuspectedMismatch("landed somewhere the plan didn't expect");
        Assert.Equal(TierLevel.Tier2, rig.Gate.CurrentTier);
        Assert.StartsWith("active — 2 candidates", rig.Gate.Tier2ForwardStatus);
        return (rig, engine);
    }

    // e. Each step, sent once the one before has landed (the gate holds a step the
    // room it would leave from has no exit for), folds with its own landing.
    [Fact]
    public void Tier2Forward_StepsInTurn_EachLandingFoldsItsOwnStep()
    {
        (TrackerWireRig rig, PlanEngine engine) = StartForward(PairWorld(split: 1));
        using TrackerWireRig _ = rig;
        engine.Plan.Enqueue(Walk[0]);
        engine.Plan.Enqueue(Walk[1]);

        Assert.True(engine.Step());
        rig.Pump();
        Assert.StartsWith("active — 2 candidates, depth 1", rig.Gate.Tier2ForwardStatus);
        Assert.True(engine.Step());
        rig.Pump();

        Assert.Equal(TierLevel.Tier1, rig.Gate.CurrentTier);
        Assert.Equal(A(2), rig.Gate.Anchor);
        AssertLocatedAt(rig, A(2));
        Assert.Empty(engine.Backtracks);
    }

    // e. A step the tracker can't place (it goes Suspect) still folds, from what its
    // landing showed; and the room the footprint settles on is a deduction, so it
    // isn't written as the profile's saved room.
    [Fact]
    public void Tier2Forward_AStepLandingSuspect_FoldsFromItsDisplay_AndIsNotSavedAsTheAnchor()
    {
        // Really in copy B: its room 2 has an exit A's lacks (shared with a decoy),
        // so the tracker, holding A, can't place the landing.
        TrackerWireRig rig = new(PairWorld(split: 6, decoyAt: 2)) { Position = B(0) };
        using TrackerWireRig _ = rig;
        CharacterProfile profile = new() { LastKnownRoom = new RoomRef(A(0).Map, A(0).Room) };
        rig.Tracker.Hydrate(profile);   // sure of A(0); really in its twin
        rig.ShowRoom();
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        rig.Gate.NoteSuspectedMismatch("landed somewhere the plan didn't expect");
        engine.Plan.Enqueue(Walk[0]);
        engine.Plan.Enqueue(Walk[1]);

        Assert.True(engine.Step());
        rig.Pump();
        Assert.True(engine.Step());
        rig.Pump();

        Assert.Equal(TierLevel.Tier1, rig.Gate.CurrentTier);
        Assert.Equal(B(2), rig.Gate.Anchor);
        AssertLocatedAt(rig, B(2));
        Assert.Equal((A(0).Map, A(0).Room), (profile.LastKnownRoom!.Map, profile.LastKnownRoom.Room));
        Assert.Empty(engine.Backtracks);
    }

    // A step the game refuses or drops never happened: it leaves the walk-back
    // history, so tier 3 can't reverse it, and the forward footprint neither folds it
    // nor is dropped over it. The engine sends the step again, as the walker does,
    // and that landing folds as the step.
    [Theory]
    [InlineData("There is no exit in that direction!")]
    [InlineData("You are typing too quickly - command ignored")]
    public void Tier2Forward_ARefusedStep_LeavesTheHistory_AndTheStepSentAgainFolds(string refusal)
    {
        (TrackerWireRig rig, PlanEngine engine) = StartForward(PairWorld(split: 1));
        using TrackerWireRig _ = rig;
        engine.Plan.Enqueue(Walk[0]);
        rig.NextAnswers.Enqueue(refusal);

        Assert.True(engine.Step());
        rig.Pump();

        Assert.Equal(A(0), rig.Position);
        Assert.Empty(rig.Gate.ExecutedSinceAnchor);
        Assert.StartsWith("active — 2 candidates, depth 0", rig.Gate.Tier2ForwardStatus);

        engine.Plan.Enqueue(Walk[0]);   // sent again
        engine.Plan.Enqueue(Walk[1]);
        Assert.True(engine.Step());
        rig.Pump();
        Assert.Equal(new[] { Walk[0] }, rig.Gate.ExecutedSinceAnchor);
        Assert.True(engine.Step());
        rig.Pump();

        Assert.Equal(TierLevel.Tier1, rig.Gate.CurrentTier);
        AssertLocatedAt(rig, A(2));
    }

    // e. A teleport breaks the walk the forward footprint folds.
    [Fact]
    public void Tier2Forward_ATeleportStep_DropsTheFootprint()
    {
        (TrackerWireRig rig, _) = StartForward(PairWorld(split: 1));
        using TrackerWireRig _rig = rig;

        rig.Gate.NoteEngineStepSent(Direction.Teleport);

        Assert.Equal("idle", rig.Gate.Tier2ForwardStatus);
    }

    // ----- tier-3 walk-back cap ------------------------------------------

    // Two long corridors of identical "Hall" rooms, one off a Gate (where the walk
    // starts), one off a Postern. No display along them can say which corridor it
    // is, so a walk-back along them never converges.
    private const int CorridorLength = 80;
    private static RoomKey CorridorA(int i) => new(1, 1000 + i);
    private static RoomKey CorridorB(int i) => new(1, 2000 + i);
    private static readonly RoomKey Gate = new(1, 1);

    // cellarWestOf sends corridor B's room there west into a Cellar instead of B's
    // room before it, so a walk-back footprint stepping west from it can tell the
    // corridors apart; B's room before it, reached only from the Cellar, still reads
    // like A's. byPosition names each room by its place along the corridor, so a
    // display fits only it and its twin.
    private static readonly RoomKey CorridorCellar = new(1, 3);

    private static string TwinCorridors(int cellarWestOf = -1, bool byPosition = false)
    {
        List<string> rooms = new()
        {
            TrackerWireRig.Room(1, 1, "Gate", ("E", Ref(CorridorA(0)))),
            TrackerWireRig.Room(1, 2, "Postern", ("E", Ref(CorridorB(0)))),
            TrackerWireRig.Room(CorridorCellar.Map, CorridorCellar.Room, "Cellar", ("E", Ref(CorridorB(Math.Max(0, cellarWestOf))))),
        };
        (Func<int, RoomKey> At, RoomKey End)[] corridors = { (CorridorA, Gate), (CorridorB, new RoomKey(1, 2)) };
        foreach ((Func<int, RoomKey> at, RoomKey end) in corridors)
            for (int i = 0; i < CorridorLength; i++)
            {
                RoomKey me = at(i);
                RoomKey west = me == CorridorB(cellarWestOf) ? CorridorCellar : i == 0 ? end : at(i - 1);
                List<(string, string)> exits = new() { ("W", Ref(west)) };
                if (i < CorridorLength - 1) exits.Add(("E", Ref(at(i + 1))));
                string name = "Hall" + (byPosition ? $" {i}" : "");
                rooms.Add(TrackerWireRig.Room(me.Map, me.Room, name, exits.ToArray()));
            }
        return TrackerWireRig.Rooms(rooms);
    }

    // The walker goes 72 rooms down a corridor before a room shows an exit the map
    // doesn't have. Well past the tier-2 budget, the gate walks back, but the
    // footprint can't converge in twin corridors: it gives up after 64 reverse
    // moves, the cap, rather than reversing all 72, and goes the way an exhausted
    // walk-back goes (the terminal `rm` ask first, then Lost).
    [Fact]
    public void Tier3_WalkBackThatNeverConverges_StopsAtTheCapAndDeclaresLost()
    {
        using TrackerWireRig rig = new(TwinCorridors()) { Position = Gate };
        List<string> terminalAsks = new();
        rig.Gate.TryResyncForced = reason => { terminalAsks.Add(reason); return false; };
        rig.ShowRoom();
        AssertLocatedAt(rig, Gate);
        rig.ExtraShownExits[CorridorA(71)] = new[] { Direction.N };

        Assert.True(rig.Walker.WalkTo(CorridorA(75)));
        rig.Pump();

        int start = rig.Log.Snapshot().ToList().FindIndex(e => e.Message.StartsWith("Tier3.start"));
        Assert.True(start >= 0, "the gate never started the walk-back");
        int reversesSent = rig.Log.Snapshot().Count(e => e.Message.StartsWith("Tier3.backtrack"));
        Assert.Equal(EngineRecoveryGate.Tier3DepthCeiling, reversesSent);
        Assert.Equal(CorridorA(71 - EngineRecoveryGate.Tier3DepthCeiling), rig.Position);

        RecoveryFailedEvent failed = Assert.Single(rig.Failures);
        Assert.Contains($"cap of {EngineRecoveryGate.Tier3DepthCeiling} reverse moves", failed.Detail);
        Assert.Contains(terminalAsks, r => r.Contains($"cap of {EngineRecoveryGate.Tier3DepthCeiling} reverse moves"));
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.StartsWith("Tier3.cap:"));
    }

    // ----- deduced locates: a guess, and a relog after one -----------------

    // What every profile save runs first (ProfileService.ProfileSaving): profiles
    // save all through play, on a buff dropping, an equipment change, a settings
    // apply, and on the way out.
    private static void Save(TrackerWireRig rig) => rig.Tracker.PersistCurrentRoomForSave();

    // A relog: the profile saves, closes and loads again, and the game shows the
    // room the character stands in. The tracker comes back at the room the save
    // left it: the known one, or a guess saved as where the trail starts.
    private static void Relog(TrackerWireRig rig, CharacterProfile profile)
    {
        Save(rig);
        rig.Tracker.OnProfileClosed();
        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();
    }

    private static RoomKey Saved(CharacterProfile profile) => new(profile.LastKnownRoom!.Map, profile.LastKnownRoom.Room);

    // B. A position the tier-3 walk-back works out is a guess (user, 2026-10-17): the
    // tracker is confirmed there, but the saved room stays the last one it knew. C. A
    // relog afterwards lands where the character is, still a guess, not in the saved
    // room the steps since never left. The engine walks twenty rooms
    // down twin corridors (every room fits itself and its twin), fails a step, then
    // reports a stall, as its watchdog does; the walk-back settles where corridor B
    // turns off into its Cellar, which no display alone tells apart.
    [Fact]
    public void Tier3_AConvergedWalkBackIsAGuess_AndARelogAfterItLandsOnTheRoomHeld()
    {
        using TrackerWireRig rig = new(TwinCorridors(cellarWestOf: 6, byPosition: true)) { Position = Gate };
        CharacterProfile profile = new() { LastKnownRoom = new RoomRef(Gate.Map, Gate.Room) };
        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        for (int i = 0; i < 20; i++)
        {
            engine.Plan.Enqueue(Direction.E);
            Assert.True(engine.Step());
            rig.Pump();
        }
        Assert.Equal(CorridorA(19), rig.Position);
        // The next step fails as a jump does: the character stays put and the map,
        // no longer sure, holds the room it stands in.
        engine.Plan.Enqueue(Direction.E);
        rig.NextAnswers.Enqueue("You fall to the ground with a thud, taking 3 damage!");
        Assert.True(engine.Step());
        rig.Pump();
        Assert.Equal(RoomConfidence.Suspect, rig.Tracker.State.Confidence);
        RoomKey? recovered = null;
        bool savedStayed = false;
        engine.Resumed = key =>
        {
            recovered = key;
            savedStayed = Saved(profile) == Gate && rig.Tracker.State.Confidence == RoomConfidence.Confirmed;
        };

        rig.Gate.NoteEngineStalled("the step's watchdog gave up");
        rig.Pump();

        Assert.Equal(CorridorA(5), recovered);
        Assert.Equal(CorridorA(5), rig.Position);
        Assert.True(savedStayed, "the walk-back's room was saved as the known room, or not confirmed");
        rig.Gate.Detach();

        rig.Do("e");
        Relog(rig, profile);

        AssertLocatedAt(rig, CorridorA(6));
        Assert.True(rig.Tracker.PositionIsGuess);
        Assert.Equal(Gate, Saved(profile));
    }

    // C. A room settled on without saving it as known (here the passive locate's
    // convergence) is a guess, and so is the room a step from it reaches. The save
    // keeps the known room and saves that one as where the trail starts, so a relog
    // lands on it, not on the saved room the character left.
    [Fact]
    public void ARelogAfterAnUnsavedReanchor_LandsOnTheRoomHeld()
    {
        using TrackerWireRig rig = new(PairWorld(split: 1)) { Position = A(0) };
        CharacterProfile profile = new() { LastKnownRoom = new RoomRef(A(0).Map, A(0).Room) };
        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();
        rig.NextAnswers.Enqueue("You fall to the ground with a thud, taking 3 damage!");
        rig.Do(Token(0));                          // the step fails; the map is unsure
        Assert.Equal(RoomConfidence.Suspect, rig.Tracker.State.Confidence);
        rig.Do(Token(0));
        rig.Do(Token(1));                          // the passive locate settles on A(2)
        AssertLocatedAt(rig, A(2));
        rig.Do(Token(2));

        Relog(rig, profile);

        AssertLocatedAt(rig, A(3));
        Assert.Equal(A(0), Saved(profile));
    }

    // C. The same after a tier-2 deduced locate. The map holds A(0), really in its
    // twin B(0); B's room 2 shows an exit A's lacks (shared with a decoy), so the
    // tracker can't place that landing and the forward footprint settles on B(2).
    // A save on the way to the relog keeps A(0).
    [Fact]
    public void Tier2Forward_ARelogAfterADeducedLocate_LandsOnTheRoomHeld()
    {
        TrackerWireRig rig = new(PairWorld(split: 6, decoyAt: 2)) { Position = B(0) };
        using TrackerWireRig _ = rig;
        CharacterProfile profile = new() { LastKnownRoom = new RoomRef(A(0).Map, A(0).Room) };
        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        rig.Gate.NoteSuspectedMismatch("landed somewhere the plan didn't expect");
        engine.Plan.Enqueue(Walk[0]);
        engine.Plan.Enqueue(Walk[1]);
        Assert.True(engine.Step());
        rig.Pump();
        Assert.True(engine.Step());
        rig.Pump();
        Assert.Equal(B(2), rig.Gate.Anchor);
        AssertLocatedAt(rig, B(2));
        Assert.True(rig.Tracker.PositionIsGuess);
        rig.Gate.Detach();

        rig.Do(Token(2));
        Relog(rig, profile);

        AssertLocatedAt(rig, B(3));
        Assert.Equal(A(0), Saved(profile));
    }

    // A save straight after a guess, before any step from it, keeps the known
    // room and saves the guess as a trail of none; the relog lands on the guess.
    [Fact]
    public void ASaveAfterAGuess_KeepsTheKnownRoom_AndARelogLandsOnTheGuess()
    {
        using TrackerWireRig rig = new(PairWorld(split: 1)) { Position = A(0) };
        CharacterProfile profile = new() { LastKnownRoom = new RoomRef(A(0).Map, A(0).Room) };
        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();
        rig.NextAnswers.Enqueue("You fall to the ground with a thud, taking 3 damage!");
        rig.Do(Token(0));
        rig.Do(Token(0));
        rig.Do(Token(1));                          // the passive locate settles on A(2)
        AssertLocatedAt(rig, A(2));

        Save(rig);

        Assert.Equal(A(0), Saved(profile));
        Assert.Null(profile.RecentSteps);
        Assert.Equal((A(2).Map, A(2).Room), (profile.RecentStepsFrom!.Map, profile.RecentStepsFrom.Room));

        Relog(rig, profile);

        AssertLocatedAt(rig, A(2));
        Assert.True(rig.Tracker.PositionIsGuess);
        Assert.Equal(A(0), Saved(profile));
    }

    // A strict confirm ends a guess: a save after it saves the room held, even
    // one reached since by a move through rooms that read alike (as a walk through
    // the Graveyard is).
    [Fact]
    public void ASaveAfterAStrictConfirm_SavesTheRoomHeld()
    {
        using TrackerWireRig rig = new(PairWorld(split: 1)) { Position = B(0) };
        CharacterProfile profile = new()
        {
            LastKnownRoom = new RoomRef(A(0).Map, A(0).Room),
            RecentStepsFrom = new RoomRef(B(0).Map, B(0).Room),   // saved after a guess at B(0)
        };
        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();
        AssertLocatedAt(rig, B(0));
        Assert.True(rig.Tracker.PositionIsGuess);

        rig.Do(Token(0));
        rig.Do(Token(1));                          // into the Cellar, which only one room reads as
        AssertLocatedAt(rig, Cellar);
        Assert.False(rig.Tracker.PositionIsGuess);
        rig.Do("s");                               // on to B(2), which reads as A(2) too
        AssertLocatedAt(rig, B(2));

        Save(rig);

        Assert.Equal(B(2), Saved(profile));
        Assert.Null(profile.RecentSteps);
        Assert.Null(profile.RecentStepsFrom);
    }

    // A trail saved as starting in a room the active game data doesn't have can't be
    // replayed from anywhere true: it is dropped, and the relog lands on the known room.
    [Fact]
    public void ARelog_WhoseTrailStartsInARoomNoLongerOnTheMap_DropsTheTrail()
    {
        using TrackerWireRig rig = new(PairWorld(split: 1)) { Position = A(0) };
        CharacterProfile profile = new()
        {
            LastKnownRoom = new RoomRef(A(0).Map, A(0).Room),
            RecentSteps = new() { new DirectionDto(Walk[0]) },
            RecentStepsFrom = new RoomRef(9, 999),
        };

        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();

        AssertLocatedAt(rig, A(0));
        Assert.False(rig.Tracker.PositionIsGuess);
        Assert.Null(profile.RecentSteps);
        Assert.Null(profile.RecentStepsFrom);
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.StartsWith("Hydrate: the saved trail starts at 9/999"));
    }

    // A guess saved at A(5) of twin corridors where every room reads alike (a save
    // right after a footprint settled there leaves this), the known room the Gate.
    private static TrackerWireRig GuessInCorridor(out CharacterProfile profile)
    {
        TrackerWireRig rig = new(TwinCorridors()) { Position = CorridorA(5) };
        profile = new()
        {
            LastKnownRoom = new RoomRef(Gate.Map, Gate.Room),
            RecentStepsFrom = new RoomRef(CorridorA(5).Map, CorridorA(5).Room),
        };
        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();
        AssertLocatedAt(rig, CorridorA(5));
        Assert.True(rig.Tracker.PositionIsGuess);
        return rig;
    }

    // Every room of the walk reads like the one it started in, so nothing on the
    // relog's first display could tell a trail's start from its end: the save keeps
    // the room held as where the relog lands, and the saved trail never grows past
    // what one save to the next walks.
    [Fact]
    public void ARelogAfterALongWalkOnFromAGuess_LandsOnTheRoomHeld_InRoomsThatReadAlike()
    {
        using TrackerWireRig rig = GuessInCorridor(out CharacterProfile profile);
        for (int step = 1; step <= 30; step++)
        {
            rig.Do("e");
            if (step % 10 != 0) continue;
            Save(rig);
            Assert.Null(profile.RecentSteps);
            Assert.Equal(CorridorA(5 + step).Room, profile.RecentStepsFrom!.Room);
        }

        Relog(rig, profile);

        AssertLocatedAt(rig, CorridorA(35));
        Assert.True(rig.Tracker.PositionIsGuess);
        Assert.Equal(Gate, Saved(profile));
    }

    // Neither the last character's trail nor its guess goes along with a swap to a
    // character with no saved room, to be saved as its own.
    [Fact]
    public void AProfileSwap_ToOneWithNoSavedRoom_TakesNoTrailOrGuessAlong()
    {
        using TrackerWireRig rig = GuessInCorridor(out _);
        for (int step = 0; step < 3; step++) rig.Do("e");

        rig.Tracker.OnProfileClosed();
        CharacterProfile next = new();
        rig.Tracker.Hydrate(next);
        Save(rig);

        Assert.False(rig.Tracker.PositionIsGuess);
        Assert.Null(next.RecentSteps);
        Assert.Null(next.RecentStepsFrom);
    }

    // The map, unsure of the room it holds, sees it again. A display only that room
    // fits makes it known; one its look-alikes fit as well leaves it a guess.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASuspectRoomShownAgain_IsKnownOnlyWhenNoOtherRoomReadsLikeIt(bool unique)
    {
        using TrackerWireRig rig = new(TwinCorridors()) { Position = unique ? Gate : CorridorA(10) };
        RoomKey other = unique ? CorridorA(10) : Gate;
        CharacterProfile profile = new()
        {
            LastKnownRoom = new RoomRef(other.Map, other.Room),
            RecentStepsFrom = new RoomRef(rig.Position.Map, rig.Position.Room),
        };
        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();
        Assert.True(rig.Tracker.PositionIsGuess);
        // An exit the map doesn't have shows: no room fits, and the map goes unsure of
        // the room it holds.
        rig.ExtraShownExits[rig.Position] = new[] { Direction.N };
        rig.Do("l");
        Assert.Equal(RoomConfidence.Suspect, rig.Tracker.State.Confidence);
        rig.ExtraShownExits.Clear();

        rig.Do("l");
        Save(rig);

        RoomKey here = rig.Position;
        AssertLocatedAt(rig, here);
        Assert.Equal(!unique, rig.Tracker.PositionIsGuess);
        Assert.Equal(unique ? here : other, Saved(profile));
    }

    // Holding no room ends a guess: a death, Lost, or the room graph built again.
    [Theory]
    [InlineData("death")]
    [InlineData("lost")]
    [InlineData("reload")]
    public void AGuess_EndsWhenNoRoomIsHeld(string how)
    {
        using TrackerWireRig rig = GuessInCorridor(out _);
        switch (how)
        {
            case "death":
                rig.NextAnswers.Enqueue("You have 8 lives left.");
                rig.Do("l");
                Assert.Equal(RoomConfidence.PendingRespawn, rig.Tracker.State.Confidence);
                break;
            case "lost":
                for (int step = 0; step < 8 && rig.Tracker.State.Confidence != RoomConfidence.Lost; step++)
                {
                    rig.NextMoveShows.Enqueue("\u001b[1;36mNowhere\u001b[0m\r\nObvious exits: east, west");
                    rig.Do("e");
                }
                Assert.Equal(RoomConfidence.Lost, rig.Tracker.State.Confidence);
                break;
            default:
                rig.Graph.OnActiveSetChanged("alpha");
                Assert.Equal(RoomConfidence.Unknown, rig.Tracker.State.Confidence);
                break;
        }

        Assert.False(rig.Tracker.PositionIsGuess);
    }

    // A trail saved as starting in the known room starts from nothing worked out.
    [Fact]
    public void ARelog_WhoseTrailStartsInTheKnownRoom_IsNoGuess()
    {
        using TrackerWireRig rig = new(TwinCorridors()) { Position = CorridorA(5) };
        CharacterProfile profile = new()
        {
            LastKnownRoom = new RoomRef(CorridorA(5).Map, CorridorA(5).Room),
            RecentStepsFrom = new RoomRef(CorridorA(5).Map, CorridorA(5).Room),
        };

        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();

        AssertLocatedAt(rig, CorridorA(5));
        Assert.False(rig.Tracker.PositionIsGuess);
    }

    // A strict anchor ends a guess on disk too: after an `rm` answer puts the map
    // sure of another room, the save holds no start worked out, and the relog
    // lands on the known room.
    [Fact]
    public void AStrictLocateAfterASavedGuess_LeavesNoWorkedOutStartSaved()
    {
        using TrackerWireRig rig = GuessInCorridor(out CharacterProfile profile);
        rig.Do("e");
        Save(rig);
        Assert.NotNull(profile.RecentStepsFrom);

        rig.Position = Gate;
        rig.Tracker.SetLocated(Gate);
        Relog(rig, profile);

        AssertLocatedAt(rig, Gate);
        Assert.False(rig.Tracker.PositionIsGuess);
        Assert.Equal(Gate, Saved(profile));
        Assert.Null(profile.RecentStepsFrom);
    }

    // ----- a flee's steps in the walk-back -------------------------------

    // Two copies of a bending corridor of "Hall" rooms, one off a Gate and one off a
    // Postern: every Hall reads like its twin, so a walk-back tells them apart only
    // once it is back at the Gate.
    private static RoomKey BendA(int i) => new(1, 10 + i);
    private static RoomKey BendB(int i) => new(1, 20 + i);
    private static readonly RoomKey Postern = new(1, 2);

    private static string BendWorld()
    {
        List<string> rooms = new()
        {
            TrackerWireRig.Room(Gate.Map, Gate.Room, "Gate", ("E", Ref(BendA(1)))),
            TrackerWireRig.Room(Postern.Map, Postern.Room, "Postern", ("E", Ref(BendB(1)))),
        };
        foreach ((Func<int, RoomKey> at, RoomKey end) in new (Func<int, RoomKey>, RoomKey)[] { (BendA, Gate), (BendB, Postern) })
        {
            RoomKey one = at(1), two = at(2), three = at(3), four = at(4);
            rooms.Add(TrackerWireRig.Room(one.Map, one.Room, "Hall", ("W", Ref(end)), ("E", Ref(two))));
            rooms.Add(TrackerWireRig.Room(two.Map, two.Room, "Hall", ("W", Ref(one)), ("N", Ref(three))));
            rooms.Add(TrackerWireRig.Room(three.Map, three.Room, "Hall", ("S", Ref(two)), ("E", Ref(four))));
            rooms.Add(TrackerWireRig.Room(four.Map, four.Room, "Hall", ("W", Ref(three))));
        }
        return TrackerWireRig.Rooms(rooms);
    }

    // A flee is a real move: its step goes in the walk-back history, wired as the
    // client wires it (HealthManager.FleeMoveSent). The engine walks E, E, N, a low-HP
    // run goes back S, the walk carries on N into a room showing an exit the map
    // lacks, and the engine's watchdog gives up. The walk-back undoes the run's step
    // too, so it retraces the way taken and settles at the Gate; reversing only the
    // engine's steps would send it south out of a room with no south exit.
    // A rig walked E, E, N from the Gate into BendA(3) by an engine, with a
    // HealthManager wired as the client wires it, its flee run through that engine.
    private static (TrackerWireRig Rig, PlanEngine Engine, Game.PlayerState State, Game.Health.HealthManager Health) WalkIntoTheBend()
    {
        TrackerWireRig rig = new(BendWorld()) { Position = Gate };
        rig.ShowRoom();
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        Game.PlayerState state = new();
        Game.Health.HealthManager health = new(state, new MovementCoordinator(rig.Log),
            readSettings: () => new HealthSettings(),
            isEnabled: () => true,
            readHangupCommand: () => string.Empty,
            getActiveMovementEngine: () => engine,
            getLastSentDirection: () => rig.Gate.LastRouteStep,
            readCombatSettings: () => new CombatSettings { RunDirection = RunDirection.Backward, RunDistance = 1 },
            readGeneralSettings: null,
            hasEngageableHostiles: () => true,
            log: rig.Log,
            hasHostileInRoom: () => true,
            post: action => action());
        health.FleeMoveSent = rig.Gate.NoteFleeStepSent;
        foreach (Direction dir in new[] { Direction.E, Direction.E, Direction.N })
        {
            engine.Plan.Enqueue(dir);
            Assert.True(engine.Step());
            rig.Pump();
        }
        Assert.Equal(BendA(3), rig.Position);
        return (rig, engine, state, health);
    }

    // HP falls in a fight: the flee's run starts.
    private static void LowHp(Game.PlayerState state)
    {
        state.MaxHp = 200;
        state.InCombat = true;
        state.HasPromptData = true;
        state.Hp = 30;
    }

    [Fact]
    public void AFleeStep_IsInTheWalkBackHistory_SoTheWalkBackRetracesIt()
    {
        (TrackerWireRig rig, PlanEngine engine, Game.PlayerState state, Game.Health.HealthManager health) = WalkIntoTheBend();
        using TrackerWireRig _ = rig;
        using Game.Health.HealthManager __ = health;

        LowHp(state);
        rig.Pump();
        Assert.Equal(BendA(2), rig.Position);
        Assert.Equal(new[] { Direction.E, Direction.E, Direction.N, Direction.S }, rig.Gate.ExecutedSinceAnchor);

        engine.Plan.Enqueue(Direction.N);
        rig.ExtraShownExits[BendA(3)] = new[] { Direction.D };
        Assert.True(engine.Step());
        rig.Pump();
        rig.ExtraShownExits.Clear();
        Assert.Equal(RoomConfidence.Suspect, rig.Tracker.State.Confidence);
        RoomKey? recovered = null;
        engine.Resumed = key => recovered = key;

        rig.Gate.NoteEngineStalled("the step's watchdog gave up");
        rig.Pump();

        Assert.Equal(Gate, recovered);
        Assert.Equal(Gate, rig.Position);
        Assert.Empty(rig.Failures);
        Assert.Equal(new[] { Direction.S, Direction.S, Direction.N, Direction.S, Direction.W, Direction.W }, engine.Backtracks);
    }

    // A flee step that never went out is no move: the engine's sends were held (the
    // send gate drops them), or the engine had stopped and sent nothing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFleeStepThatNeverWentOut_IsNotInTheWalkBackHistory(bool sendsHeld)
    {
        (TrackerWireRig rig, PlanEngine engine, Game.PlayerState state, Game.Health.HealthManager health) = WalkIntoTheBend();
        using TrackerWireRig _ = rig;
        using Game.Health.HealthManager __ = health;
        Services.EngineSendGate sends = new();
        engine.Wire = sends.WrapEngineSender(rig.Send, routeSteps: true);
        health.EngineSendsHeld = () => sends.IsLocked;
        if (sendsHeld) sends.Hold("a dropped character");
        else engine.Stopped = true;

        LowHp(state);
        rig.Pump();

        Assert.Equal(BendA(3), rig.Position);
        Assert.Equal(new[] { Direction.E, Direction.E, Direction.N }, rig.Gate.ExecutedSinceAnchor);
    }

    // A flee with no way planned turns back along the last step of the route. It
    // never takes the flee's own step for that: reversed, it would run straight back
    // into the room just fled.
    [Fact]
    public void AFleeWithNoWayPlanned_TurnsBackAlongTheRoute_NotTheFlee()
    {
        (TrackerWireRig rig, PlanEngine engine, Game.PlayerState state, Game.Health.HealthManager health) = WalkIntoTheBend();
        using TrackerWireRig _ = rig;
        using Game.Health.HealthManager __ = health;

        LowHp(state);
        rig.Pump();

        Assert.Equal(Direction.S, engine.Backtracks.Single());
        Assert.Equal(Direction.S, rig.Gate.ExecutedSinceAnchor[^1]);
        Assert.Equal(Direction.N, rig.Gate.LastRouteStep);
    }

    // ----- where tier 3 starts -------------------------------------------

    // The map holds A(0), really in its twin B(0), and follows the walk as A's. B's
    // room 2 shows an exit A's lacks (shared with a decoy), so the map, unsure, goes
    // on holding A(1), the room the step left; the character stands in B(2). Seeded
    // from what that landing showed, the walk-back's first reverse step tells B(2)
    // from the decoy. Seeded from the room held, it is a step out of line, and the
    // footprint runs out at once.
    [Fact]
    public void Tier3_AnUnsureMap_StartsTheWalkBackFromTheFailedStepsLanding()
    {
        using TrackerWireRig rig = new(PairWorld(split: 6, decoyAt: 2)) { Position = B(0) };
        CharacterProfile profile = new() { LastKnownRoom = new RoomRef(A(0).Map, A(0).Room) };
        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        engine.Plan.Enqueue(Walk[0]);
        engine.Plan.Enqueue(Walk[1]);
        Assert.True(engine.Step());
        rig.Pump();
        Assert.True(engine.Step());
        rig.Pump();
        Assert.Equal(B(2), rig.Position);
        Assert.Equal(RoomConfidence.Suspect, rig.Tracker.State.Confidence);
        Assert.Equal(A(1), rig.Believed);
        RoomKey? recovered = null;
        engine.Resumed = key => recovered = key;

        rig.Gate.NoteEngineStalled("the step's watchdog gave up");
        rig.Pump();

        Assert.Empty(rig.Failures);
        Assert.Equal(B(1), recovered);
        AssertLocatedAt(rig, B(1));
    }

    // ----- a stale re-confirm in rooms that read alike --------------------

    // The map, unsure, holds A(10) of corridors where every room reads alike. The
    // character walks on east: the display fits the room held as well as any, but it
    // shows where the move went. Re-confirming the room held would put the map back
    // a room behind; it stays unsure until a display only one room fits.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMovesDisplay_ThatFitsTheSuspectRoomAndItsLookAlikes_DoesNotReconfirmIt(bool echo)
    {
        using TrackerWireRig rig = new(TwinCorridors()) { Position = CorridorA(10), Echo = echo };
        CharacterProfile profile = new() { LastKnownRoom = new RoomRef(CorridorA(10).Map, CorridorA(10).Room) };
        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();
        // An exit the map doesn't have shows: no room fits, and the map goes unsure of
        // the room it holds.
        rig.ExtraShownExits[rig.Position] = new[] { Direction.N };
        rig.Do("l");
        rig.ExtraShownExits.Clear();
        Assert.Equal(RoomConfidence.Suspect, rig.Tracker.State.Confidence);

        rig.Do("e");

        Assert.Equal(CorridorA(11), rig.Position);
        Assert.NotEqual(RoomConfidence.Confirmed, rig.Tracker.State.Confidence);

        for (int step = 0; step < 12; step++) rig.Do("w");
        AssertLocatedAt(rig, Gate);
    }

    // ----- saves around a relog and a profile swap -----------------------

    // A relog lands on the trail's start and replays the trail against the first
    // display. A save before that display (the profile saves all through play)
    // keeps the trail rather than saving the start as the room the character is in.
    [Fact]
    public void ASaveRightAfterARelog_KeepsTheTrailForTheFirstDisplay()
    {
        using TrackerWireRig rig = new(TwinCorridors(byPosition: true)) { Position = CorridorA(8) };
        CharacterProfile profile = new()
        {
            LastKnownRoom = new RoomRef(CorridorA(5).Map, CorridorA(5).Room),
            RecentSteps = new() { new DirectionDto(Direction.E), new DirectionDto(Direction.E), new DirectionDto(Direction.E) },
        };
        rig.Tracker.Hydrate(profile);

        Save(rig);
        Assert.Equal(3, profile.RecentSteps?.Count);
        Assert.Equal(CorridorA(5), Saved(profile));
        rig.ShowRoom();

        AssertLocatedAt(rig, CorridorA(8));
    }

    // A character loaded with no saved room doesn't take on the room the tracker
    // held for the last one: its first save, before any room is shown, writes no
    // room as its own.
    [Fact]
    public void AProfileSwap_ToOneWithNoSavedRoom_DoesNotSaveTheLastCharactersRoom()
    {
        using TrackerWireRig rig = new(TwinCorridors()) { Position = Gate };
        CharacterProfile first = new();
        rig.Tracker.Hydrate(first);
        rig.ShowRoom();
        AssertLocatedAt(rig, Gate);
        Assert.Equal(Gate, Saved(first));

        Save(rig);
        rig.Tracker.OnProfileClosed();
        CharacterProfile next = new();
        rig.Tracker.Hydrate(next);
        Save(rig);

        Assert.Null(next.LastKnownRoom);
        Assert.Null(rig.Believed);
        Assert.Equal(RoomConfidence.Unknown, rig.Tracker.State.Confidence);
    }

    // A character that never had a room saved as known (only rooms it worked out)
    // still has the guess its last save kept: a relog lands there, as a guess.
    [Fact]
    public void ARelog_WithAGuessSavedAndNoKnownRoom_LandsOnTheGuess()
    {
        using TrackerWireRig rig = new(TwinCorridors()) { Position = CorridorA(5) };
        CharacterProfile profile = new() { RecentStepsFrom = new RoomRef(CorridorA(5).Map, CorridorA(5).Room) };

        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();

        AssertLocatedAt(rig, CorridorA(5));
        Assert.True(rig.Tracker.PositionIsGuess);
        Assert.Null(profile.LastKnownRoom);
    }

    // A guess saved as where a relog lands (a trail of none) ends with the guess:
    // holding no room (Lost, the room graph built again) takes it off the profile,
    // so a relog lands on the known room rather than on the old guess.
    [Theory]
    [InlineData("lost")]
    [InlineData("reload")]
    public void AGuessEndingWithNoRoomHeld_TakesItsSavedStartWithIt(string how)
    {
        using TrackerWireRig rig = GuessInCorridor(out CharacterProfile profile);
        Assert.NotNull(profile.RecentStepsFrom);
        if (how == "lost")
        {
            // Displays no room fits, with nothing moved: the map strikes out to Lost.
            foreach (Direction shown in new[] { Direction.N, Direction.S, Direction.U })
            {
                rig.ExtraShownExits[rig.Position] = new[] { shown };
                rig.Do("l");
            }
            Assert.Equal(RoomConfidence.Lost, rig.Tracker.State.Confidence);
        }
        else
        {
            rig.Graph.OnActiveSetChanged("alpha");
            Assert.Equal(RoomConfidence.Unknown, rig.Tracker.State.Confidence);
        }

        Assert.Null(profile.RecentStepsFrom);
        rig.Tracker.OnProfileClosed();
        rig.Tracker.Hydrate(profile);
        Assert.Equal(Gate, rig.Believed);
        Assert.False(rig.Tracker.PositionIsGuess);
    }

    // ----- the walk-back's own moves -------------------------------------

    // Twin corridors where every room is "Hall", walked twenty rooms east from the
    // Gate (no display says how far along either corridor a room is, so a walk-back
    // there settles back at the Gate); the next step fails as a
    // jump does, the map goes unsure, and the engine's watchdog gives up: the gate
    // walks back. Returns the engine, with the first reverse move on the wire.
    private static PlanEngine WalkBackInHalls(TrackerWireRig rig)
    {
        CharacterProfile profile = new() { LastKnownRoom = new RoomRef(Gate.Map, Gate.Room) };
        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        for (int i = 0; i < 20; i++)
        {
            engine.Plan.Enqueue(Direction.E);
            Assert.True(engine.Step());
            rig.Pump();
        }
        engine.Plan.Enqueue(Direction.E);
        rig.NextAnswers.Enqueue("You fall to the ground with a thud, taking 3 damage!");
        Assert.True(engine.Step());
        rig.Pump();
        Assert.Equal(RoomConfidence.Suspect, rig.Tracker.State.Confidence);
        rig.Gate.NoteEngineStalled("the step's watchdog gave up");
        return engine;
    }

    // The gate reads each reverse move's landing as the room is parsed, before the
    // tracker has. The next reverse move goes out only once the tracker has read
    // that landing too: sent first, it is counted in the trail the tracker replays
    // against that display, and in rooms that read alike the map confirms a room one
    // step ahead of the character.
    [Fact]
    public void Tier3_TheNextReverseMoveWaitsForTheTrackerToReadTheLanding()
    {
        using TrackerWireRig rig = new(TwinCorridors(cellarWestOf: 6)) { Position = Gate };
        List<string> confirmedWrong = new();
        rig.Tracker.StateChanged += t =>
        {
            if (t.NewConfidence == RoomConfidence.Confirmed && t.NewRoom is { } room && room.Key != rig.Position)
                confirmedWrong.Add($"{room.Key} while at {rig.Position}");
        };
        PlanEngine engine = WalkBackInHalls(rig);
        RoomKey? recovered = null;
        engine.Resumed = key => recovered = key;

        rig.Pump();

        Assert.Empty(rig.Failures);
        Assert.Equal(Gate, recovered);
        Assert.Equal(Gate, rig.Position);
        Assert.Empty(confirmedWrong);
    }

    // A reverse move the game drops (typing too fast) never ran: it goes out once
    // more and the walk-back carries on. One the game refuses can't retrace the way
    // back: the walk-back ends as an exhausted one does, never waiting on a landing
    // that won't come.
    [Theory]
    [InlineData("You are typing too quickly - command ignored", true)]
    [InlineData("There is no exit in that direction!", false)]
    public void Tier3_AReverseMoveTheGameRefuses_IsSentAgainOrEndsTheWalkBack(string refusal, bool carriesOn)
    {
        using TrackerWireRig rig = new(TwinCorridors(cellarWestOf: 6)) { Position = Gate };
        PlanEngine engine = WalkBackInHalls(rig);
        RoomKey? recovered = null;
        engine.Resumed = key => recovered = key;
        rig.NextAnswers.Enqueue(refusal);

        rig.Pump();

        if (carriesOn)
        {
            Assert.Empty(rig.Failures);
            Assert.Equal(Gate, recovered);
            Assert.Equal(Gate, rig.Position);
        }
        else
        {
            RecoveryFailedEvent failed = Assert.Single(rig.Failures);
            Assert.Contains("refused", failed.Detail);
            Assert.Null(recovered);
            Assert.Equal(CorridorA(19), rig.Position);
        }
    }

    // A reverse move the game answers with nothing the client reads: the walk-back
    // waits for its landing a bounded time, then ends as an exhausted one does.
    [Fact]
    public void Tier3_AReverseMoveWithNoAnswer_EndsTheWalkBackAfterTheWait()
    {
        using TrackerWireRig rig = new(TwinCorridors(cellarWestOf: 6)) { Position = Gate };
        WalkBackInHalls(rig);
        rig.NextAnswers.Enqueue("");
        rig.Pump();
        Assert.Empty(rig.Failures);

        rig.Timers.Advance(rig.Gate.ReverseLandingTimeout);

        RecoveryFailedEvent failed = Assert.Single(rig.Failures);
        Assert.Contains("no answer", failed.Detail);
    }

    // The same when the first thing the reconnect shows is a room too dark to see:
    // nothing proves the step sent before the drop landed, so it counts as not
    // taken. The map stays on the room it left, and the step leaves the history.
    [Fact]
    public void AStepBeforeADrop_ADarkRoomAfterTheReconnect_CountsItAsNotTaken()
    {
        using TrackerWireRig rig = new(TwinCorridors()) { Position = Gate };
        rig.ShowRoom();
        AssertLocatedAt(rig, Gate);
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        engine.Plan.Enqueue(Direction.E);
        rig.NextAnswers.Enqueue("");   // the link goes before the game answers
        Assert.True(engine.Step());
        rig.Pump();
        Assert.Equal(RoomConfidence.Pending, rig.Tracker.State.Confidence);

        rig.Tracker.NoteConnectionLost();
        rig.DarkRooms.Add(Gate);
        rig.ShowRoom();

        Assert.True(rig.Tracker.IsInDarkRoom);
        AssertLocatedAt(rig, Gate);
        Assert.Empty(rig.Gate.ExecutedSinceAnchor);
    }

    // A blind-move line is the same: it can't say which room it is.
    [Fact]
    public void AStepBeforeADrop_ABlindLineAfterTheReconnect_CountsItAsNotTaken()
    {
        using TrackerWireRig rig = new(TwinCorridors()) { Position = Gate };
        rig.ShowRoom();
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        engine.Plan.Enqueue(Direction.E);
        rig.NextAnswers.Enqueue("");   // the link goes before the game answers
        Assert.True(engine.Step());
        rig.Pump();

        rig.Tracker.NoteConnectionLost();
        rig.NextAnswers.Enqueue("You are blind.");
        rig.Do("l");

        AssertLocatedAt(rig, Gate);
        Assert.Empty(rig.Gate.ExecutedSinceAnchor);
    }

    // Several moves in flight when the link went, and the reconnect shows the room
    // they all left (lit or dark): every one of them is off the trail and out of
    // the walk-back history, not only the newest.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeveralStepsBeforeADrop_AllLeaveTheTrailAndTheHistory(bool dark)
    {
        using TrackerWireRig rig = new(TwinCorridors()) { Position = Gate };
        CharacterProfile profile = new();
        rig.Tracker.Hydrate(profile);
        rig.ShowRoom();
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        engine.Plan.Enqueue(Direction.E);
        engine.Plan.Enqueue(Direction.E);
        rig.NextAnswers.Enqueue("");   // the link goes before the game answers either
        rig.NextAnswers.Enqueue("");
        Assert.True(engine.Step());
        Assert.True(engine.Step());
        rig.Pump();
        Assert.Equal(2, profile.RecentSteps?.Count);

        rig.Tracker.NoteConnectionLost();
        if (dark) rig.DarkRooms.Add(Gate);
        rig.ShowRoom();

        AssertLocatedAt(rig, Gate);
        Assert.Empty(rig.Gate.ExecutedSinceAnchor);
        Assert.Null(profile.RecentSteps);
    }

    // Several moves in flight at the drop, in rooms that read alike: the display
    // after the reconnect fits the room they left and the rooms they lead to, so it
    // can't say how many were taken. None is un-counted, and the map goes unsure.
    [Fact]
    public void SeveralStepsBeforeADrop_InRoomsThatReadAlike_NoneIsUnCounted()
    {
        using TrackerWireRig rig = new(TwinCorridors()) { Position = CorridorA(5) };
        rig.Tracker.Hydrate(new CharacterProfile { LastKnownRoom = new RoomRef(CorridorA(5).Map, CorridorA(5).Room) });
        rig.ShowRoom();
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        engine.Plan.Enqueue(Direction.E);
        engine.Plan.Enqueue(Direction.E);
        rig.NextAnswers.Enqueue("");   // the link goes before the game answers either
        rig.NextAnswers.Enqueue("");
        Assert.True(engine.Step());
        Assert.True(engine.Step());
        rig.Pump();

        rig.Tracker.NoteConnectionLost();
        rig.ShowRoom();

        Assert.Equal(RoomConfidence.Suspect, rig.Tracker.State.Confidence);
        Assert.Equal(new[] { Direction.E, Direction.E }, rig.Gate.ExecutedSinceAnchor);
    }

    // A move sent while the map was already unsure of the room, then the drop: the
    // first display after the reconnect un-counts it, as a dark or blind line does.
    [Fact]
    public void AStepSentWhileUnsure_ADisplayAfterTheReconnect_UnCountsIt()
    {
        using TrackerWireRig rig = new(TwinCorridors()) { Position = CorridorA(10) };
        rig.Tracker.Hydrate(new CharacterProfile { LastKnownRoom = new RoomRef(CorridorA(10).Map, CorridorA(10).Room) });
        rig.ShowRoom();
        rig.ExtraShownExits[rig.Position] = new[] { Direction.N };
        rig.Do("l");
        rig.ExtraShownExits.Clear();
        Assert.Equal(RoomConfidence.Suspect, rig.Tracker.State.Confidence);
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        engine.Plan.Enqueue(Direction.E);
        rig.NextAnswers.Enqueue("");   // the link goes before the game answers
        Assert.True(engine.Step());
        rig.Pump();
        Assert.Equal(new[] { Direction.E }, rig.Gate.ExecutedSinceAnchor);

        rig.Tracker.NoteConnectionLost();
        rig.ShowRoom();

        Assert.Empty(rig.Gate.ExecutedSinceAnchor);
    }

    // The walk-back ended (no answer in time, or a refusal) and the last `rm` is out:
    // a display that comes before its answer is no landing of the walk-back's. It
    // sends no reverse move and folds nothing.
    [Theory]
    [InlineData("")]
    [InlineData("There is no exit in that direction!")]
    public void Tier3_EndedWithTheLastRmOut_ADisplayBeforeItsAnswerSendsNoReverseMove(string answer)
    {
        using TrackerWireRig rig = new(TwinCorridors(cellarWestOf: 6)) { Position = Gate };
        rig.Gate.TryResyncForced = reason => reason.StartsWith("last resort");
        PlanEngine engine = WalkBackInHalls(rig);
        rig.NextAnswers.Enqueue(answer);
        rig.Pump();
        if (answer.Length == 0) rig.Timers.Advance(rig.Gate.ReverseLandingTimeout);
        Assert.True(rig.Gate.AwaitingAuthoritativeResync);
        Assert.Empty(rig.Failures);
        int sent = engine.Backtracks.Count;

        rig.Do("l");

        Assert.Equal(sent, engine.Backtracks.Count);
        Assert.Equal("idle", rig.Gate.Tier3Status);
    }

    // The walk-back ended on a refusal and the last `rm` is out; meanwhile the map
    // becomes sure of its room. A room only one place fits resumes the engine there
    // at once; one worked out (its trail replayed) is resumed from when that `rm`
    // fails. Either way the client isn't declared Lost while the map is sure.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tier3_TheMapSureWhileTheLastRmIsOut_ResumesRatherThanLost(bool roomOnlyOnePlaceFits)
    {
        using TrackerWireRig rig = new(TwinCorridors(cellarWestOf: 6)) { Position = Gate };
        rig.Gate.TryResyncForced = reason => reason.StartsWith("last resort");
        PlanEngine engine = WalkBackInHalls(rig);
        rig.NextAnswers.Enqueue("There is no exit in that direction!");
        rig.Pump();
        Assert.True(rig.Gate.AwaitingAuthoritativeResync);
        RoomKey? resumed = null;
        engine.Resumed = key => resumed = key;

        if (roomOnlyOnePlaceFits)
        {
            rig.Position = Gate;
            rig.ShowRoom();
            Assert.Equal(Gate, resumed);
            Assert.False(rig.Gate.AwaitingAuthoritativeResync);
        }
        else
        {
            rig.Do("w");
            rig.Do("w");   // the map replays its trail and settles where the walk went
            AssertLocatedAt(rig, CorridorA(17));
            Assert.Null(resumed);
        }
        rig.Gate.OnAuthoritativeResyncFailed();

        Assert.Empty(rig.Failures);
        Assert.Empty(engine.Aborts);
        Assert.Equal(roomOnlyOnePlaceFits ? Gate : CorridorA(17), resumed);
    }

    // A reverse move held for the tracker to read a display the tracker never reads
    // (a RoomParsed listener ahead of it threw): the wait for its landing still runs
    // out and ends the walk-back.
    [Fact]
    public void Tier3_AReverseMoveHeldForADisplayNeverRead_StillEndsAfterTheWait()
    {
        using TrackerWireRig rig = new(TwinCorridors(cellarWestOf: 6)) { Position = Gate };
        WalkBackInHalls(rig);

        rig.Gate.OnRoomObserved(new RoomObservation("Hall", new HashSet<Direction> { Direction.W, Direction.E }));
        Assert.Contains("held until the map reads the landing", rig.Gate.Tier3Status);
        rig.Timers.Advance(rig.Gate.ReverseLandingTimeout);

        RecoveryFailedEvent failed = Assert.Single(rig.Failures);
        Assert.Contains("no answer", failed.Detail);
    }

    // A step sent just before the connection dropped, which the first display after
    // the reconnect shows never landed, leaves the walk-back history.
    [Fact]
    public void AStepLostToADroppedConnection_LeavesTheWalkBackHistory()
    {
        using TrackerWireRig rig = new(PairWorld(split: 1)) { Position = A(0) };
        rig.Tracker.SetLocated(A(0));
        rig.ShowRoom();
        PlanEngine engine = new(rig);
        rig.Gate.Attach(engine);
        engine.Plan.Enqueue(Walk[0]);
        rig.NextAnswers.Enqueue("");   // the link goes before the game answers
        Assert.True(engine.Step());
        rig.Pump();
        Assert.Equal(new[] { Walk[0] }, rig.Gate.ExecutedSinceAnchor);

        rig.Tracker.NoteConnectionLost();
        rig.ShowRoom();

        Assert.Equal(A(0), rig.Position);
        AssertLocatedAt(rig, A(0));
        Assert.Empty(rig.Gate.ExecutedSinceAnchor);
    }
}
