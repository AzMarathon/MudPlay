using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Health;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// MonsterHangupWatcher: a monster whose relationship is Hangup, on the room
// roster, is answered with the health settings' hang-up, once per sighting, and
// the watch is off for a minute after a reconnect that follows such a hang-up.
public sealed class MonsterHangupWatcherTests
{
    private const int Ogre = 7;
    private const int Rat = 8;
    private const int Troll = 9;

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public RoomEntryWatcher Arrivals { get; }
        public MonsterHangupWatcher Watcher { get; }

        public Dictionary<int, MonsterRelationship> Relationships { get; } = new();
        public bool OverlayUnreadable { get; set; }
        public bool AtBoardMenu { get; set; }

        // What HealthManager.HangUpForMonster answers. Jumped by default: an
        // escape that went out and left the session up, so a test of sightings is
        // not also a test of the drop that follows a hang-up.
        public EscapeOutcome Outcome { get; set; } = EscapeOutcome.Jumped;
        public List<string> HangUps { get; } = new();

        public DateTimeOffset Clock { get; set; } = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public List<Action> Scheduled { get; } = new();
        public List<string> Notices { get; } = new();
        public int HoldChanges { get; private set; }
        public List<LogEntry> Logged { get; } = new();

        public Harness(Func<string, EscapeOutcome>? hangUp = null)
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, new PlayerDatabase(), Log);
            Watcher = new MonsterHangupWatcher(
                Classifier,
                resolveOverlay: ResolveOverlay,
                hangUp: hangUp ?? (reason => { HangUps.Add(reason); return Outcome; }),
                atBoardMenu: () => AtBoardMenu,
                describeRoom: () => "in Town Square (1/5)",
                schedule: (_, callback) => Scheduled.Add(callback),
                log: Log,
                now: () => Clock);
            Watcher.HoldNotice += Notices.Add;
            Watcher.HoldChanged += () => HoldChanges++;
            Arrivals = new RoomEntryWatcher(Router, Classifier, Log);
            Log.EntryAdded += Logged.Add;

            AddMonster(Ogre, "ogre");
            AddMonster(Rat, "giant rat");
            AddMonster(Troll, "troll");
        }

        private MonsterOverlay ResolveOverlay(int number)
        {
            if (OverlayUnreadable) throw new InvalidOperationException("no active set");
            return Relationships.TryGetValue(number, out MonsterRelationship rel)
                ? new MonsterOverlay { Relationship = rel }
                : new MonsterOverlay();
        }

        public void AddMonster(int number, string name) =>
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}", Name: name, Links: new[] { new GameDataLink("Monsters", number) }));

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        // One second passes and the countdown's timer fires.
        public void Tick(int seconds = 1)
        {
            for (int i = 0; i < seconds; i++)
            {
                Clock += TimeSpan.FromSeconds(1);
                List<Action> due = new(Scheduled);
                Scheduled.Clear();
                foreach (Action callback in due) callback();
            }
        }

        // The watcher hangs up for the ogre, and the line drops for it.
        public void HangUpAndDrop()
        {
            Outcome = EscapeOutcome.HungUp;
            Relationships[Ogre] = MonsterRelationship.Hangup;
            Feed("Also here: ogre.");
            Classifier.NoteGameLeft();
            Watcher.NoteDisconnected();
        }

        public IEnumerable<string> InfoLines =>
            Logged.Where(e => e.Source == MonsterHangupWatcher.LogCategory && e.Severity == LogSeverity.Info)
                  .Select(e => e.Message);

        public IEnumerable<string> WarnLines =>
            Logged.Where(e => e.Source == MonsterHangupWatcher.LogCategory && e.Severity == LogSeverity.Warn)
                  .Select(e => e.Message);

        public void Dispose()
        {
            Arrivals.Dispose();
            Watcher.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- what is seen --------------------------------------------------

    [Fact]
    public void HangupMonsterOnTheRoomDisplay_HangsUp_AndSaysWhichAndWhere()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: giant rat, ogre.");

        Assert.Equal("ogre (#7) is here, relationship Hangup", Assert.Single(h.HangUps));
        string line = Assert.Single(h.InfoLines);
        Assert.Contains("ogre (#7) seen in Town Square (1/5)", line);
        Assert.Contains("hung up", line);
        Assert.Contains("ogre (#7) seen in Town Square (1/5)", h.Watcher.LastSighting);
    }

    [Fact]
    public void ItIsTheSight_NotAnAttack()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("The ogre swings at you!");
        Assert.Empty(h.HangUps);

        h.Feed("Also here: ogre.");
        Assert.Single(h.HangUps);
    }

    [Fact]
    public void AnArrivalLineThatNamesIt_HangsUp()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: giant rat.");
        Assert.Empty(h.HangUps);

        h.Feed("An ogre stomps into the room from southeast.");

        Assert.Single(h.HangUps);
    }

    [Fact]
    public void AFlavorPrefixedName_ReadsTheSameRecord()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: large ogre.");

        Assert.Equal("large ogre (#7) is here, relationship Hangup", Assert.Single(h.HangUps));
    }

    [Theory]
    [InlineData(MonsterRelationship.Enemy)]
    [InlineData(MonsterRelationship.Neutral)]
    [InlineData(MonsterRelationship.Friend)]
    [InlineData(MonsterRelationship.Flee)]
    public void EveryOtherRelationship_DrawsNothing(MonsterRelationship relationship)
    {
        using Harness h = new();
        h.Relationships[Ogre] = relationship;

        h.Feed("Also here: ogre, giant rat.");

        Assert.Empty(h.HangUps);
        Assert.Empty(h.InfoLines);
        Assert.Equal("(none this session)", h.Watcher.LastSighting);
    }

    // The relationship belongs to a monster record. A name on the roster that
    // resolves to none has no relationship to read.
    [Fact]
    public void ANameThatResolvesToNoRecord_DrawsNothing()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: gorgon.");

        Assert.Empty(h.HangUps);
    }

    [Fact]
    public void AnOverlayThatCannotBeRead_DrawsNothing_AndIsSaidOnce()
    {
        using Harness h = new() { OverlayUnreadable = true };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Feed("Also here: ogre, giant rat.");

        Assert.Empty(h.HangUps);
        Assert.Contains("could not be read", Assert.Single(h.WarnLines));
    }

    // ----- once per sighting --------------------------------------------

    [Fact]
    public void OneAnswerPerRoomDisplay_WhateverReissuesItsRoster()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: giant rat, ogre.");
        h.Classifier.ReemitCurrent();
        h.Feed("A giant rat scurries into the room from north.");
        h.Classifier.RemoveDeadEntity("giant rat");
        h.Classifier.ReemitCurrent();

        Assert.Single(h.HangUps);
        Assert.Single(h.InfoLines);
    }

    // The next room's roster replaces this one's with no empty roster between
    // (its "Also here:" line is read before the move is confirmed), and an escape
    // that left the session up has to be asked for again there.
    [Fact]
    public void TheNextRoomsDisplay_IsANewSighting_WithNoEmptyRosterBetween()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Equal(2, h.HangUps.Count);
    }

    [Fact]
    public void TwoHangupMonsters_OneDying_IsStillTheSameSighting()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Relationships[Troll] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre, troll.");
        h.Classifier.RemoveDeadEntity("ogre");
        h.Classifier.ReemitCurrent();

        Assert.Equal("ogre (#7) is here, relationship Hangup", Assert.Single(h.HangUps));
    }

    [Fact]
    public void AHangupMonsterThatWasNotThereWhenAnswered_IsAnswered()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Relationships[Troll] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Feed("A troll lumbers into the room from north.");

        Assert.Equal(2, h.HangUps.Count);
        Assert.StartsWith("troll (#9)", h.HangUps[1]);
    }

    // A name the roster could not place is looked at again once `who` supplies a
    // record. That is the same room display with one more monster known on it:
    // the one already answered is not answered again, the new one is.
    [Fact]
    public void AMonsterPlacedLater_InsideTheSameDisplay_IsAnsweredOnce()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre, gorgon.");
        Assert.Single(h.HangUps);

        h.AddMonster(10, "gorgon");
        h.Relationships[10] = MonsterRelationship.Hangup;
        h.Classifier.ReclassifyUnknown();
        h.Classifier.ReclassifyUnknown();
        h.Classifier.ReemitCurrent();

        Assert.Equal(2, h.HangUps.Count);
        Assert.StartsWith("gorgon (#10)", h.HangUps[1]);
    }

    [Fact]
    public void ARosterWithoutIt_EndsTheSighting()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: giant rat, ogre.");

        h.Classifier.RemoveDepartedEntity("ogre");
        h.Feed("An ogre stomps into the room from southeast.");

        Assert.Equal(2, h.HangUps.Count);
    }

    [Fact]
    public void AHangUpThatDidNotGoOut_IsSaid_AndNotRetriedThisSighting()
    {
        using Harness h = new() { Outcome = EscapeOutcome.NotSent };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Single(h.HangUps);
        Assert.Contains("did not go out", h.Watcher.LastSighting);
        Assert.Single(h.WarnLines);
    }

    // ----- while the line is dropping -----------------------------------

    [Fact]
    public void WhileTheLineIsDropping_NothingMoreIsSent()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Relationships[Troll] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Feed("A troll lumbers into the room from north.");
        h.Feed("Also here: ogre, troll.");

        Assert.Single(h.HangUps);
    }

    // The client closes the line itself, so this is the hang-up that did not
    // take: the watch cannot stay off for good on the strength of it.
    [Fact]
    public void AHangUpWithNoDropAfterIt_PutsTheWatchBackOn()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Clock += TimeSpan.FromSeconds(11);
        h.Feed("Also here: ogre.");

        Assert.Equal(2, h.HangUps.Count);
        Assert.Contains("did not drop", Assert.Single(h.WarnLines));
    }

    // ----- a dropped connection -----------------------------------------

    // The roster used to outlive the link, and anything that re-issued it while
    // offline or at the login (the Auto-Combat toggle, a PvP fight ending) was
    // read as the monster seen again.
    [Fact]
    public void AfterOurHangUpDropsTheLine_AReissuedRosterSendsNothing()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Watcher.NoteDisconnected();
        h.Classifier.ReemitCurrent();

        Assert.Single(h.HangUps);
    }

    [Fact]
    public void NothingIsSeenWhileDisconnected()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HangupsDisabled };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");
        h.HangUps.Clear();

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        h.Outcome = EscapeOutcome.HungUp;
        h.Classifier.ReemitCurrent();

        Assert.Null(h.Classifier.Current);
        Assert.Empty(h.HangUps);
    }

    // The monster left while we were away and the room is empty, so no "Also
    // here:" line rebuilds the roster: the first arrival must not be added to it.
    [Fact]
    public void AnArrivalAfterTheReconnect_IsNotAddedToTheOldRoster()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HangupsDisabled };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");
        h.HangUps.Clear();

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        h.Outcome = EscapeOutcome.HungUp;
        h.Feed("A giant rat scurries into the room from north.");

        Assert.Equal("giant rat", Assert.Single(h.Classifier.Current!.Value.Entities).ResolvedName);
        Assert.Empty(h.HangUps);
    }

    // ----- the minute after a reconnect ---------------------------------

    [Fact]
    public void AfterReconnecting_TheWatchIsOffForAMinute_FromTheFirstGamePrompt()
    {
        using Harness h = new();
        h.HangUpAndDrop();
        Assert.Equal("watch off: the minute starts at the first game prompt after the reconnect", h.Watcher.DescribeHold());
        Assert.Null(h.Watcher.HoldText);

        // Back in the same room, the monster still there: the room is displayed
        // ahead of the first prompt.
        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();
        Assert.Single(h.HangUps);
        Assert.Single(h.InfoLines, line => line.Contains("the watch is off after the reconnect"));

        h.Clock += TimeSpan.FromMinutes(5);   // however long the login took
        h.Watcher.NoteInGamePrompt();
        Assert.Equal("Hangup watch off 1:00", h.Watcher.HoldText);
        Assert.Equal(60, h.Watcher.HoldSecondsLeft);
        Assert.Contains("Hangup watch off for 60 seconds", Assert.Single(h.Notices));
        Assert.Equal(1, h.HoldChanges);

        h.Tick();
        Assert.Equal("Hangup watch off 0:59", h.Watcher.HoldText);
        Assert.Equal("watch off, 59s left", h.Watcher.DescribeHold());
        Assert.Equal(2, h.HoldChanges);

        h.Feed("Also here: ogre.");
        h.Tick(58);
        Assert.Equal("Hangup watch off 0:01", h.Watcher.HoldText);
        Assert.Single(h.HangUps);
    }

    [Fact]
    public void WhenTheMinuteEnds_AMonsterStillThereIsAnswered()
    {
        using Harness h = new();
        h.HangUpAndDrop();
        h.Feed("Also here: ogre.");
        h.Watcher.NoteInGamePrompt();

        h.Tick(60);

        Assert.Null(h.Watcher.HoldText);
        Assert.Equal("none", h.Watcher.DescribeHold());
        Assert.Equal(new[] { "Hangup watch back on" }, h.Notices.Skip(1));
        Assert.Equal(2, h.HangUps.Count);
        Assert.Empty(h.Scheduled);
    }

    [Fact]
    public void WhenTheMinuteEnds_AnEmptyRoomSendsNothing()
    {
        using Harness h = new();
        h.HangUpAndDrop();
        h.Watcher.NoteInGamePrompt();

        h.Tick(60);

        Assert.Null(h.Watcher.HoldText);
        Assert.Single(h.HangUps);
    }

    // A second hang-up and reconnect starts a minute of its own.
    [Fact]
    public void EachHangUpAndReconnect_StartsItsOwnMinute()
    {
        using Harness h = new();
        h.HangUpAndDrop();
        h.Feed("Also here: ogre.");
        h.Watcher.NoteInGamePrompt();
        h.Tick(60);
        Assert.Equal(2, h.HangUps.Count);

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        h.Feed("Also here: ogre.");
        h.Watcher.NoteInGamePrompt();

        Assert.Equal("Hangup watch off 1:00", h.Watcher.HoldText);
        Assert.Equal(2, h.HangUps.Count);
    }

    // A prompt between the exit command and the drop is not the reconnect.
    [Fact]
    public void APromptBeforeTheLineDrops_DoesNotStartTheMinute()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Watcher.NoteInGamePrompt();

        Assert.Null(h.Watcher.HoldText);
        Assert.Empty(h.Notices);
    }

    // Another path's escape had just gone out when the monster was seen: the
    // sighting is answered and nothing more is asked for while its roster stands.
    [Fact]
    public void AnEscapeAlreadyUnderWay_AnswersTheSighting()
    {
        using Harness h = new() { Outcome = EscapeOutcome.AlreadyEscaping };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Outcome = EscapeOutcome.HungUp;
        h.Classifier.ReemitCurrent();

        Assert.Single(h.HangUps);
        Assert.Contains("another escape had just gone out", Assert.Single(h.InfoLines));
        Assert.Empty(h.WarnLines);
    }

    // A low-HP or PvP hang-up had gone out a moment before the monster was seen,
    // and the line drops for it: the monster was in sight, so the reconnect gets
    // its minute all the same.
    [Fact]
    public void ADropRightAfterSeeingItDuringAnotherEscape_TurnsTheWatchOff()
    {
        using Harness h = new() { Outcome = EscapeOutcome.AlreadyEscaping };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        h.Outcome = EscapeOutcome.HungUp;
        h.Feed("Also here: ogre.");
        h.Watcher.NoteInGamePrompt();

        Assert.Equal("Hangup watch off 1:00", h.Watcher.HoldText);
        Assert.Single(h.HangUps);
    }

    // The other escape may have been a wimpy jump, which drops no line: nothing is
    // waited on, nothing is warned about, and a drop long after is not ours.
    [Fact]
    public void SeeingItDuringAnotherEscape_OwesNoDrop()
    {
        using Harness h = new() { Outcome = EscapeOutcome.AlreadyEscaping };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Clock += TimeSpan.FromSeconds(11);
        h.Outcome = EscapeOutcome.Jumped;
        h.Feed("Also here: ogre.");
        Assert.Equal(2, h.HangUps.Count);
        Assert.Empty(h.WarnLines);

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        Assert.Equal("none", h.Watcher.DescribeHold());
    }

    // The minute is for a reconnect the user makes. The PvP response's own
    // dial-back enters the game unattended, so it gets none.
    [Fact]
    public void AnAutomaticReconnect_GetsNoMinute()
    {
        using Harness h = new();
        h.HangUpAndDrop();

        h.Watcher.CancelHold();
        h.Watcher.CancelHold();
        Assert.Equal("none", h.Watcher.DescribeHold());
        Assert.Single(h.InfoLines, line => line.Contains("the reconnect is automatic"));

        h.Watcher.NoteInGamePrompt();
        h.Feed("Also here: ogre.");

        Assert.Null(h.Watcher.HoldText);
        Assert.Empty(h.Notices);
        Assert.Equal(2, h.HangUps.Count);
    }

    // Out at the board's menu when the minute ends, nothing is sent there; the
    // monster is answered on the way back into the game.
    [Fact]
    public void WhenTheMinuteEndsAtTheBoardsMenu_TheAnswerWaitsForTheGame()
    {
        using Harness h = new();
        h.HangUpAndDrop();
        h.Watcher.NoteInGamePrompt();
        h.Feed("Also here: ogre.");

        h.AtBoardMenu = true;
        h.Tick(60);
        Assert.Null(h.Watcher.HoldText);
        Assert.Single(h.HangUps);
        Assert.Single(h.InfoLines, line => line.Contains("board's menu"));

        h.AtBoardMenu = false;
        h.Watcher.NoteBackInGame();
        Assert.Equal(2, h.HangUps.Count);
    }

    // A carrier loss inside the minute neither restarts nor ends it: it runs out
    // on the clock, offline or not, and the next room display is answered.
    [Fact]
    public void ASecondDropDuringTheMinute_LetsItRunOut()
    {
        using Harness h = new();
        h.HangUpAndDrop();
        h.Watcher.NoteInGamePrompt();
        h.Feed("Also here: ogre.");
        h.Tick(10);

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        Assert.Equal("Hangup watch off 0:50", h.Watcher.HoldText);

        h.Tick(50);
        Assert.Null(h.Watcher.HoldText);
        Assert.Equal("none", h.Watcher.DescribeHold());
        Assert.Equal("Hangup watch back on", h.Notices.Last());
        Assert.Single(h.HangUps);

        h.Watcher.NoteInGamePrompt();
        h.Feed("Also here: ogre.");
        Assert.Null(h.Watcher.HoldText);
        Assert.Equal(2, h.HangUps.Count);
    }

    [Fact]
    public void ADisposedWatcher_CountsNothingDown()
    {
        Harness h = new();
        h.HangUpAndDrop();
        h.Watcher.NoteInGamePrompt();
        int changes = h.HoldChanges;

        h.Dispose();
        h.Tick(60);

        Assert.Equal(changes, h.HoldChanges);
        Assert.Single(h.Notices);
    }

    [Theory]
    [InlineData(EscapeOutcome.Jumped)]
    [InlineData(EscapeOutcome.NotSent)]
    [InlineData(EscapeOutcome.HangupsDisabled)]
    public void ADropThatWasNotOurHangUp_TurnsNothingOff(EscapeOutcome outcome)
    {
        using Harness h = new() { Outcome = outcome };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");
        h.HangUps.Clear();

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        h.Watcher.NoteInGamePrompt();
        h.Outcome = EscapeOutcome.HungUp;
        h.Feed("Also here: ogre.");

        Assert.Null(h.Watcher.HoldText);
        Assert.Equal("none", h.Watcher.DescribeHold());
        Assert.Single(h.HangUps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherCharacter_HasNoHold_AndNoLastSighting(bool minuteRunning)
    {
        using Harness h = new();
        h.HangUpAndDrop();
        if (minuteRunning) h.Watcher.NoteInGamePrompt();
        int changes = h.HoldChanges;

        h.Watcher.Reset();

        Assert.Null(h.Watcher.HoldText);
        Assert.Equal("none", h.Watcher.DescribeHold());
        Assert.Equal("(none this session)", h.Watcher.LastSighting);
        Assert.Equal(minuteRunning ? changes + 1 : changes, h.HoldChanges);

        // The old minute's timer has nothing left to do, and the next prompt
        // starts none.
        h.Tick(60);
        h.Watcher.NoteInGamePrompt();
        Assert.Null(h.Watcher.HoldText);
        Assert.Equal(minuteRunning ? 1 : 0, h.Notices.Count);

        h.Feed("Also here: ogre.");
        Assert.Equal(2, h.HangUps.Count);
    }

    // ----- what stops it -------------------------------------------------

    [Theory]
    [InlineData(EscapeOutcome.HangupsDisabled, "Disable Hangups is on")]
    [InlineData(EscapeOutcome.AllOff, "Allow hangup in all-off mode is not ticked")]
    public void HeldByASwitch_SaidOncePerSighting_AndAnsweredOnceItIsLifted(EscapeOutcome held, string why)
    {
        using Harness h = new() { Outcome = held };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();
        h.Feed("A giant rat scurries into the room from north.");

        string line = Assert.Single(h.InfoLines);
        Assert.Contains("ogre (#7) seen in Town Square (1/5)", line);
        Assert.Contains(why, line);
        Assert.Contains(why, h.Watcher.LastSighting);

        h.HangUps.Clear();
        h.Outcome = EscapeOutcome.HungUp;
        h.Classifier.ReemitCurrent();

        Assert.Single(h.HangUps);
        Assert.Contains("hung up", h.InfoLines.Last());
    }

    // At the board's menu the exit command would be a menu selection.
    [Fact]
    public void AtTheBoardsMenu_NothingIsAskedFor()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HangupsDisabled };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");
        h.HangUps.Clear();

        h.AtBoardMenu = true;
        h.Outcome = EscapeOutcome.HungUp;
        h.Classifier.ReemitCurrent();
        h.Classifier.ReemitCurrent();

        Assert.Empty(h.HangUps);
        Assert.Single(h.InfoLines, line => line.Contains("board's menu"));
    }

    // The whole path through HealthManager: the exit command, the carrier drop and
    // the penalty line after it, and the two switches that stop the low-HP hang-up
    // stopping this one.
    [Theory]
    [InlineData(true, false, false, 1)]    // an Auto on
    [InlineData(true, true, false, 0)]     // Disable Hangups
    [InlineData(false, false, false, 0)]   // all off
    [InlineData(false, false, true, 1)]    // all off, Allow hangup in all-off mode
    [InlineData(false, true, true, 0)]     // Disable Hangups outranks it
    public void SightGoesThroughTheHealthHangUp(bool healthEngineOn, bool hangupsDisabled, bool allowInAllOff, int expected)
    {
        GeneralSettings general = new() { DisableHangups = hangupsDisabled, AllowHangupInAllOffMode = allowInAllOff };
        LogService log = new();
        List<string> wire = new();
        List<bool> penaltyAsked = new();
        int carrierDrops = 0;
        HangupSignal signal = new();
        using HealthManager health = new(
            new PlayerState(), new MovementCoordinator(log),
            readSettings: () => new HealthSettings(),
            isEnabled: () => healthEngineOn,
            readHangupCommand: () => "=x",
            getActiveMovementEngine: null,
            getLastSentDirection: null,
            readCombatSettings: null,
            readGeneralSettings: () => general,
            hasEngageableHostiles: null,
            log: log,
            hangupSignal: signal);
        health.SetWireSender(bytes => wire.Add(System.Text.Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        health.SetHangupDisconnect(() => carrierDrops++);
        health.SetHangupPenaltyLog(pvp =>
        {
            Assert.Contains("=x", wire);
            penaltyAsked.Add(pvp);
        });
        using Harness h = new(hangUp: health.HangUpForMonster);
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Equal(expected, wire.Count(w => w == "=x"));
        Assert.Equal(expected, carrierDrops);
        Assert.Equal(expected, penaltyAsked.Count);
        Assert.DoesNotContain(true, penaltyAsked);
        Assert.Equal(expected == 1, signal.PeekForTests().DisconnectExpected);
    }
}
