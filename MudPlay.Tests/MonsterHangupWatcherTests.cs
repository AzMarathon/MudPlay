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
// roster, is answered with the health settings' hang-up, once per sighting.
public sealed class MonsterHangupWatcherTests
{
    private const int Ogre = 7;
    private const int Rat = 8;

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public RoomEntryWatcher Arrivals { get; }
        public MonsterHangupWatcher Watcher { get; }

        public Dictionary<int, MonsterRelationship> Relationships { get; } = new();
        public bool HangupsDisabled { get; set; }
        public bool OverlayUnreadable { get; set; }
        public bool HangUpWorks { get; set; } = true;
        public List<string> HangUps { get; } = new();
        public List<LogEntry> Logged { get; } = new();

        public Harness(Func<string, bool>? hangUp = null, Func<bool>? hangupsDisabled = null)
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, new PlayerDatabase(), Log);
            Watcher = new MonsterHangupWatcher(
                Classifier,
                resolveOverlay: ResolveOverlay,
                hangupsDisabled: hangupsDisabled ?? (() => HangupsDisabled),
                hangUp: hangUp ?? (reason => { HangUps.Add(reason); return HangUpWorks; }),
                describeRoom: () => "in Town Square (1/5)",
                log: Log);
            Arrivals = new RoomEntryWatcher(Router, Classifier, Log);
            Log.EntryAdded += Logged.Add;

            AddMonster(Ogre, "ogre");
            AddMonster(Rat, "giant rat");
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

        public IEnumerable<string> InfoLines =>
            Logged.Where(e => e.Source == MonsterHangupWatcher.LogCategory && e.Severity == LogSeverity.Info)
                  .Select(e => e.Message);

        public void Dispose()
        {
            Arrivals.Dispose();
            Watcher.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- what is a sighting -------------------------------------------

    [Fact]
    public void HangupMonsterOnTheRoomDisplay_HangsUp_AndSaysWhichAndWhere()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: giant rat, ogre.");

        Assert.Equal("ogre (#7) is here, relationship Hangup", Assert.Single(h.HangUps));
        string line = Assert.Single(h.InfoLines);
        Assert.Contains("ogre (#7)", line);
        Assert.Contains("in Town Square (1/5)", line);
        Assert.Contains("hanging up", line);
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

        h.Feed("Also here: troll.");

        Assert.Empty(h.HangUps);
    }

    [Fact]
    public void AnOverlayThatCannotBeRead_DrawsNothing()
    {
        using Harness h = new() { OverlayUnreadable = true };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");

        Assert.Empty(h.HangUps);
    }

    // ----- once per sighting --------------------------------------------

    [Fact]
    public void OneHangUpPerSighting_WhateverReissuesTheRoster()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: giant rat, ogre.");
        h.Classifier.ReemitCurrent();
        h.Feed("Also here: giant rat, ogre.");
        h.Feed("A giant rat scurries into the room from north.");
        h.Classifier.RemoveDeadEntity("giant rat");

        Assert.Single(h.HangUps);
        Assert.Single(h.InfoLines);
    }

    [Fact]
    public void ARosterWithoutIt_EndsTheSighting()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Classifier.NoteRoomChanged();
        h.Feed("Also here: ogre.");

        Assert.Equal(2, h.HangUps.Count);
    }

    // The roster outlives a dropped connection, so the drop itself has to end the
    // sighting: back in the same room with the same monster, it is seen afresh.
    [Fact]
    public void AfterADisconnect_TheSameMonsterIsANewSighting()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");
        h.Feed("Also here: ogre.");
        Assert.Single(h.HangUps);

        h.Watcher.NoteDisconnected();
        h.Feed("Also here: ogre.");

        Assert.Equal(2, h.HangUps.Count);
    }

    // ----- Disable Hangups ----------------------------------------------

    [Fact]
    public void DisableHangups_NoHangUp_SaidOncePerSighting()
    {
        using Harness h = new() { HangupsDisabled = true };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Feed("Also here: ogre.");

        Assert.Empty(h.HangUps);
        string line = Assert.Single(h.InfoLines);
        Assert.Contains("ogre (#7)", line);
        Assert.Contains("in Town Square (1/5)", line);
        Assert.Contains("Disable Hangups is on", line);
        Assert.Contains("Disable Hangups is on", h.Watcher.LastSighting);
    }

    [Fact]
    public void DisableHangupsTurnedOff_TheNextRosterIsAnswered()
    {
        using Harness h = new() { HangupsDisabled = true };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.HangupsDisabled = false;
        h.Feed("Also here: ogre.");

        Assert.Single(h.HangUps);
    }

    // ----- the hang-up itself -------------------------------------------

    [Fact]
    public void AHangUpThatDidNotGoOut_IsSaid_AndNotRetriedThisSighting()
    {
        using Harness h = new() { HangUpWorks = false };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Feed("Also here: ogre.");

        Assert.Single(h.HangUps);
        Assert.Contains("did not go out", h.Watcher.LastSighting);
        Assert.Contains(h.Logged, e =>
            e.Source == MonsterHangupWatcher.LogCategory && e.Severity == LogSeverity.Warn);
    }

    // The whole path: the sight sends the exit command through HealthManager, the
    // penalty line is asked for after it, and Disable Hangups stops both.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void SightGoesThroughTheHealthHangUp_UnlessHangupsAreDisabled(bool disabled, int expected)
    {
        GeneralSettings general = new() { DisableHangups = disabled };
        LogService log = new();
        List<string> wire = new();
        List<bool> penaltyAsked = new();
        int carrierDrops = 0;
        HangupSignal signal = new();
        using HealthManager health = new(
            new PlayerState(), new MovementCoordinator(log),
            readSettings: () => new HealthSettings(),
            isEnabled: () => false,
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
        using Harness h = new(hangUp: health.HangUpForMonster, hangupsDisabled: () => general.DisableHangups);
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Feed("Also here: ogre.");

        Assert.Equal(expected, wire.Count(w => w == "=x"));
        Assert.Equal(expected, carrierDrops);
        Assert.Equal(expected, penaltyAsked.Count);
        Assert.DoesNotContain(true, penaltyAsked);
        Assert.Equal(!disabled, signal.PeekForTests().DisconnectExpected);
    }
}
