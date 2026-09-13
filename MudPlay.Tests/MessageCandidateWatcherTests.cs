using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class MessageCandidateWatcherTests
{
    private sealed class Harness
    {
        public LogService Log { get; } = new();
        public MessageRouter Router { get; } = new();
        public MessageStore Messages { get; } = new();
        public MessageCandidateStore Candidates { get; } = new();
        public MessageCandidateWatcher Watcher { get; }

        // Mutable so a test can point the watcher at a known room before feeding.
        public RoomKey? Room { get; set; }

        // Stand-in for the active set's Rooms-table name index — a test adds a
        // room name here to prove a room-display title line isn't staged.
        public HashSet<string> RoomNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Default in-game so capture tests exercise the real path; a test that
        // needs the pre-game gate passes inGame:false. seedDefaultPatterns loads the
        // real catalog for tests about shapes DefaultPatterns is supposed to cover.
        public Harness(bool inGame = true, bool seedDefaultPatterns = false)
        {
            if (seedDefaultPatterns) DefaultPatterns.Seed(Router);
            Watcher = new MessageCandidateWatcher(
                Router, Messages, Candidates, currentRoom: () => Room, log: Log,
                isKnownRoomName: RoomNames.Contains,
                isRecognizedByDirectParser: PartyManager.IsRosterRow);
            if (inGame) Watcher.NotifyInGame();
        }

        // The watcher subscribes to LineExtractor in real life; tests reflect into
        // the private OnLine directly instead of standing up a fake extractor —
        // same pattern ConditionTrackerTests uses for the identical shape.
        //
        // flush drives the private CommitPending: real capture holds a vetted line
        // back one line so an experience gain can retire it as monster death flavour,
        // so a test asserting on a single fed line has to release it. Tests about the
        // death rule itself pass flush:false and feed the following line themselves.
        public void Feed(string text, DateTimeOffset? when = null, bool flush = true)
        {
            var emitted = new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                when ?? DateTimeOffset.UtcNow, IsPromptLine: false);
            Invoke("OnLine", emitted);
            if (flush) Invoke("CommitPending");
        }

        private void Invoke(string method, params object[] args) =>
            typeof(MessageCandidateWatcher)
                .GetMethod(method,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .Invoke(Watcher, args);
    }

    private static MessageRecord MakeRecord(string casterMessage) => new(
        Id: MessageRecord.ComputeId("Test", casterMessage, "", "", "", ""),
        Name: "Test",
        Flags: MessageFlags.None,
        RawFlagsHex: 0,
        CasterMessage: casterMessage,
        TargetMessage: string.Empty,
        WitnessMessage: string.Empty,
        AppliedMessage: string.Empty,
        AppliedEndsWith: string.Empty);

    [Fact]
    public void KnownMessageLine_DoesNotCreateCandidate()
    {
        Harness h = new();
        h.Messages.Messages.Add(MakeRecord("You feel a surge of power!"));

        h.Feed("You feel a surge of power!");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void ConfuseFumbleLine_DoesNotCreateCandidate()
    {
        // A recognized fumble line reaches the app via a predicate, not a router
        // pattern — the watcher must still exclude it (indexed from the record's
        // ConfuseFumbleLine slot), or it'd be falsely staged as unrecognized.
        Harness h = new();
        h.Messages.Messages.Add(new MessageRecord(
            Id: MessageRecord.ComputeId("Convulsions", "", "", "", "", ""),
            Name: "Convulsions",
            Flags: MessageFlags.Confused,
            RawFlagsHex: 0,
            CasterMessage: string.Empty,
            TargetMessage: string.Empty,
            WitnessMessage: string.Empty,
            AppliedMessage: string.Empty,
            AppliedEndsWith: string.Empty,
            Links: null,
            ConfuseFumbleLine: "You look around stupidly."));

        h.Feed("You look around stupidly.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void RouterMatchedLine_DoesNotCreateCandidate()
    {
        Harness h = new();
        h.Router.RegisterPattern(new PrefixPattern("test.gossip", "*GOSSIP* "));

        h.Feed("*GOSSIP* Forged: hello");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void GenuinelyNewLine_CreatesCandidate_AndWarnsOnce()
    {
        Harness h = new();
        int warnCount = 0;
        h.Log.EntryAdded += e => { if (e.Severity == LogSeverity.Warn) warnCount++; };

        h.Feed("A shimmering aura surrounds you!");

        Assert.Single(h.Candidates.Candidates);
        Assert.Equal(1, h.Candidates.Candidates[0].Occurrences);
        Assert.Equal(1, warnCount);
    }

    [Fact]
    public void NewLine_TagsCandidateWithCurrentRoom()
    {
        Harness h = new();
        h.Room = new RoomKey(12, 3456);

        h.Feed("A shimmering aura surrounds you!");

        MessageCandidateRecord c = Assert.Single(h.Candidates.Candidates);
        Assert.Equal(12, c.Map);
        Assert.Equal(3456, c.Room);
    }

    [Fact]
    public void NewLine_WithoutKnownRoom_LeavesLocationNull()
    {
        Harness h = new();   // Room stays null

        h.Feed("A shimmering aura surrounds you!");

        MessageCandidateRecord c = Assert.Single(h.Candidates.Candidates);
        Assert.Null(c.Map);
        Assert.Null(c.Room);
    }

    [Fact]
    public void RepeatedLine_BumpsOccurrences_WarnsOnlyOnce()
    {
        Harness h = new();
        int warnCount = 0;
        h.Log.EntryAdded += e => { if (e.Severity == LogSeverity.Warn) warnCount++; };

        h.Feed("A shimmering aura surrounds you!");
        h.Feed("A shimmering aura surrounds you!");
        h.Feed("A shimmering aura surrounds you!");

        Assert.Single(h.Candidates.Candidates);
        Assert.Equal(3, h.Candidates.Candidates[0].Occurrences);
        Assert.Equal(1, warnCount);
    }

    [Fact]
    public void SimulateCapture_StagesAFreshCandidateEachCall()
    {
        Harness h = new();

        string first = h.Watcher.SimulateCapture();
        string second = h.Watcher.SimulateCapture();

        Assert.NotEqual(first, second);   // varies per call → distinct candidates
        Assert.Equal(2, h.Candidates.Candidates.Count);
        Assert.Contains(h.Candidates.Candidates, c => c.RawText == first);
    }

    [Fact]
    public void SimulateCapture_RespectsDisabledGate()
    {
        Harness h = new();
        h.Watcher.Enabled = false;

        h.Watcher.SimulateCapture();

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void DismissedLine_IsIgnoredOnRecurrence()
    {
        Harness h = new();
        h.Feed("A shimmering aura surrounds you!");         // stages candidate (occ 1)
        MessageCandidateRecord c = Assert.Single(h.Candidates.Candidates);
        h.Candidates.Dismiss(c.Id);

        int warnAfter = 0;
        h.Log.EntryAdded += e => { if (e.Severity == LogSeverity.Warn) warnAfter++; };
        h.Feed("A shimmering aura surrounds you!");         // recurrence of a dismissed line

        Assert.Single(h.Candidates.Candidates);             // no duplicate
        Assert.Equal(1, h.Candidates.Candidates[0].Occurrences);  // not bumped
        Assert.Equal(0, warnAfter);                         // no re-alert
    }

    [Fact]
    public void DisabledWatcher_NeverCreatesCandidates()
    {
        Harness h = new();
        h.Watcher.Enabled = false;

        h.Feed("Whatever this is, it should be ignored.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void BurstOfDistinctUnrecognizedLines_CapsAtBurstLimit()
    {
        // BurstCap = 6, BurstWindow = 1500ms: 10 distinct never-seen lines
        // arriving within the window should stage only the first 6.
        Harness h = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        for (int i = 0; i < 10; i++)
            h.Feed($"Distinct never-seen line #{i}", t0.AddMilliseconds(i * 50));

        Assert.Equal(6, h.Candidates.Candidates.Count);
    }

    [Fact]
    public void BurstAcrossTwoWindows_BothGroupsStageNormally()
    {
        // Two separate bursts of 5 (under the cap), well apart in time, should
        // each stage in full — the window resets rather than accumulating
        // across the gap.
        Harness h = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        for (int i = 0; i < 5; i++)
            h.Feed($"Group A line #{i}", t0.AddMilliseconds(i * 50));

        DateTimeOffset t1 = t0.AddSeconds(2);   // past the 1500ms burst window
        for (int i = 0; i < 5; i++)
            h.Feed($"Group B line #{i}", t1.AddMilliseconds(i * 50));

        Assert.Equal(10, h.Candidates.Candidates.Count);
    }

    [Fact]
    public void PreGameLine_IsNotStaged()
    {
        // Before the first in-game prompt (splash / login menu / connect banner),
        // capture holds off entirely.
        Harness h = new(inGame: false);

        h.Feed("Welcome to the BBS! Press ENTER to continue.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void NotifyInGame_OpensCaptureAfterThePreGameGate()
    {
        Harness h = new(inGame: false);
        h.Feed("Login menu junk that should be ignored.");
        Assert.Empty(h.Candidates.Candidates);

        h.Watcher.NotifyInGame();
        h.Feed("A shimmering aura surrounds you!");

        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void KnownRoomName_IsNotStaged()
    {
        // A room-display title line is read directly by the room-display parser and
        // registers no router pattern, so without the Rooms-table exclusion it would
        // stage as unrecognized. A known room name is dropped...
        Harness h = new();
        h.RoomNames.Add("Intersection of Guild St. & River St.");

        h.Feed("Intersection of Guild St. & River St.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void SpellLineSharingRoomNameColour_IsStillStaged()
    {
        // ...but a genuine unrecognized spell/monster line that is NOT a room name
        // still stages, even though it can share the room-name colour on some
        // palettes — the exclusion keys off the Rooms table, never the colour.
        Harness h = new();
        h.RoomNames.Add("River Street");

        h.Feed("The goblin shaman chants a guttural incantation!");

        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void ClientStatusLine_IsNotStaged()
    {
        // The client's own bracketed WriteTerminalStatus notices are never server
        // messages, so a full-line "[ … ]" is dropped.
        Harness h = new();

        h.Feed("[The Cleric's Quest is Now Available]");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void EchoedCommand_IsNotStaged()
    {
        // A command the user just sent bounces back as an echo; ObserveOutbound
        // remembers it so the echo isn't staged as an unknown line.
        Harness h = new();
        h.Watcher.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("eq engraved warhorn\r\n"));

        h.Feed("eq engraved warhorn");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void EchoedCommand_PastEchoWindow_IsStagedAgain()
    {
        // The echo suppression is time-bounded — a line matching an old command
        // long after the fact is treated as a genuine unrecognized line.
        Harness h = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        h.Watcher.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("wave banner\r\n"));

        h.Feed("wave banner", t0.AddSeconds(30));   // well past the 3s echo window

        Assert.Single(h.Candidates.Candidates);
    }

    // ----- Templated catalogue slots -------------------------------------
    // The catalogue stores most messages as templates, which can never string-equal
    // a real line. Comparing them as text meant every templated message in the game
    // read as unrecognized and known casts were staged for review.

    private static MessageRecord MakeTemplateRecord(
        string name, string caster = "", string witness = "",
        string applied = "", string endsWith = "") => new(
            Id: MessageRecord.ComputeId(name, caster, "", witness, applied, endsWith),
            Name: name,
            Flags: MessageFlags.None,
            RawFlagsHex: 0,
            CasterMessage: caster,
            TargetMessage: string.Empty,
            WitnessMessage: witness,
            AppliedMessage: applied,
            AppliedEndsWith: endsWith);

    [Theory]
    [InlineData("Raijin casts minor healing on Raijin!")]
    [InlineData("Raijin casts bless on Suijin!")]
    public void TemplatedWitnessCast_IsNotStaged(string line)
    {
        Harness h = new();
        h.Messages.Messages.Add(MakeTemplateRecord(
            "Minor Healing", witness: "{source} casts {spellname} on {target}!"));

        h.Feed(line);

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void TemplatedCasterLine_IsNotStaged()
    {
        Harness h = new();
        h.Messages.Messages.Add(MakeTemplateRecord(
            "Way of the Swan", caster: "You invoke the {spellname}."));

        h.Feed("You invoke the way of the swan.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void LiteralAppliedEndsWith_IsMatchedAsSubstring()
    {
        // The stored wording omits the server's trailing punctuation, so an exact
        // comparison never fired and every buff-expiry line looked unrecognized.
        Harness h = new();
        h.Messages.Messages.Add(MakeTemplateRecord(
            "Way of the Tiger",
            applied: "You feel the power of the tiger.",
            endsWith: "The effects of way of the tiger wear off"));

        h.Feed("The effects of way of the tiger wear off!");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void PlaceholderOnlyTemplate_DoesNotSuppressUnrelatedLines()
    {
        // A template with no literal text compiles to a pattern matching virtually
        // any line. Indexing one would silently disable the whole feature, so it has
        // to be dropped — the stock catalogue really does contain one.
        Harness h = new();
        h.Messages.Messages.Add(MakeTemplateRecord("Degenerate", witness: "{source} {target}"));

        h.Feed("The gnarled tree groans ominously.");

        Assert.Single(h.Candidates.Candidates);
    }

    // ----- Shapes the client already knows -------------------------------

    [Theory]
    [InlineData("The room is dimly lit")]
    [InlineData("The room is dimly lit.")]
    [InlineData("The room is barely visible")]
    [InlineData("The room is pitch black")]
    [InlineData("The room is very dark - you can't see anything")]
    public void RoomLightAnnouncement_IsNotStaged(string line)
    {
        // Room light is fully derivable from game data, so none of the bands belong
        // in a review queue. "Dimly lit" alone was the noisiest line captured.
        Harness h = new();

        h.Feed(line);

        Assert.Empty(h.Candidates.Candidates);
    }

    [Theory]
    [InlineData("  Raijin WuzHere                 (Priest)     [M:100%] [H: 85%]   - Backrank")]
    [InlineData("  Suijin WuzHere                 (Witchunter)          [H:100%]   - Midrank")]
    [InlineData("  Fujin WuzHere                  (Mystic)     [K:100%] [H: 94%]   - Frontrank")]
    [InlineData("  Raijin WuzHere                 (Priest)     [Invited]")]
    public void PartyRosterRow_IsNotStaged(string line)
    {
        // `par` output is consumed by PartyManager's stateful block parser, which
        // registers no router pattern — so the whole roster staged on every poll.
        Harness h = new();

        h.Feed(line);

        Assert.Empty(h.Candidates.Candidates);
    }

    [Theory]
    [InlineData("The wild dog snaps at Suijin with its teeth!")]
    [InlineData("The dark goblin archer shoots an arrow at Suijin with their shortbow!")]
    [InlineData("The nasty bandit swings at Suijin with their broadsword!")]
    [InlineData("Raijin swipes at dark goblin archer!")]
    public void ThirdPartyPhysicalAttack_IsNotStaged(string line)
    {
        Harness h = new(seedDefaultPatterns: true);

        h.Feed(line);

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void MonsterCastAtAPartyMember_IsStillStaged()
    {
        // The physical-attack patterns must stay narrow enough to let a monster's
        // SPELL through — unrecognized monster spell messages are the whole point of
        // the capture, so a pattern that swallowed one would defeat the feature.
        Harness h = new(seedDefaultPatterns: true);

        h.Feed("The dark elf priest hurls a searing bolt at Suijin!");

        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void ThirdPartyCastFizzle_IsNotStaged()
    {
        Harness h = new(seedDefaultPatterns: true);

        h.Feed("Raijin attempted to cast minor healing, but failed.");

        Assert.Empty(h.Candidates.Candidates);
    }

    // ----- Monster death flavour ------------------------------------------

    [Theory]
    [InlineData("The dog yelps loudly, and dies.")]
    [InlineData("The dark goblin archer collapses with a spiteful hiss.")]
    public void DeathFlavourBeforeExperienceLine_IsNotStaged(string deathLine)
    {
        // Realms author death messages per species and don't publish them, so no
        // catalogue can hold one. Position identifies them: the line immediately
        // before the experience gain.
        Harness h = new(seedDefaultPatterns: true);

        h.Feed(deathLine, flush: false);
        h.Feed("You gain 350 experience.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void DeathFlavour_RetiresARowCapturedEarlier()
    {
        // Rows staged before the positional rule existed heal themselves on the next
        // kill rather than sitting in the queue forever.
        Harness h = new(seedDefaultPatterns: true);
        h.Feed("The dog yelps loudly, and dies.");
        Assert.Single(h.Candidates.Candidates);

        h.Feed("The dog yelps loudly, and dies.", flush: false);
        h.Feed("You gain 350 experience.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void DeathFlavour_SurvivesABlankLineBeforeTheExperienceGain()
    {
        // Only a substantive server line releases the held candidate. A blank line or
        // the client's own status notice between the two would otherwise let the death
        // message through.
        Harness h = new(seedDefaultPatterns: true);

        h.Feed("The dog yelps loudly, and dies.", flush: false);
        h.Feed("", flush: false);
        h.Feed("[Something the client printed]", flush: false);
        h.Feed("You gain 350 experience.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void LineNotFollowedByExperience_IsStagedNormally()
    {
        // The deferral must not swallow ordinary lines — only the one an experience
        // gain directly follows.
        Harness h = new(seedDefaultPatterns: true);

        h.Feed("The gnarled tree groans ominously.", flush: false);
        h.Feed("A cold wind stirs the branches.", flush: false);

        Assert.Single(h.Candidates.Candidates);
        Assert.Equal("The gnarled tree groans ominously.", h.Candidates.Candidates[0].RawText);
    }

    // ----- Room-spell flavour must survive --------------------------------

    [Theory]
    [InlineData("An ominous wind blows through the trees")]
    [InlineData("A flock of birds fly overhead.")]
    [InlineData("The forest becomes strangely silent.")]
    [InlineData("The leaves begin to rustle, as if some beast were about to spring forth!")]
    public void RoomSpellFlavour_IsStillStaged(string line)
    {
        // These read like scenery but are room-spell triggers the catalogue doesn't
        // have yet — surfacing them is exactly what the capture is for, so none of
        // the new exclusions may touch them.
        Harness h = new(seedDefaultPatterns: true);

        h.Feed(line);

        Assert.Single(h.Candidates.Candidates);
    }
}
