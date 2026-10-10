using MudPlay.Game.Combat;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// MonsterDeathWatcher recognizes deaths generically from the exp signal: a kill's
// "You gain N experience." followed by *Combat Off* within a window. Per-monster
// DeathLine matching was retired — death messages are arbitrary flavor with no shared
// keyword/colour, so the exp line is the only reliable generic signal (our own
// targeting names the mob).
public sealed class MonsterDeathWatcherTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public MonsterDeathWatcher Watcher { get; }
        public List<MonsterDeathEvent> Events { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Watcher = new MonsterDeathWatcher(Router, Log);
            Watcher.MonsterDied += Events.Add;
        }

        public void FeedRouter(string line)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        // A command as the server echoes it: the prompt row, then its trailing
        // text as a second line carrying the same timestamp.
        public void FeedEchoed(string command)
        {
            DateTimeOffset at = DateTimeOffset.UtcNow;
            Router.Dispatch(new LineExtractor.EmittedLine(
                "[HP=724/MA=343]:", Array.Empty<CellAttributes>(), at, IsPromptLine: true));
            Router.Dispatch(new LineExtractor.EmittedLine(
                command, Array.Empty<CellAttributes>(), at, IsPromptLine: false));
        }

        public void Dispose() => Watcher.Dispose();
    }

    [Fact]
    public void ExpThenCombatOff_FiresDeath()
    {
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
        MonsterDeathEvent evt = h.Events[0];
        Assert.True(evt.IsFallback);
        Assert.Empty(evt.Candidates);
        Assert.Equal(9, evt.ExperienceGained);
    }

    [Fact]
    public void CombatOffWithoutRecentExp_DoesNotFire()
    {
        using Harness h = new();

        h.FeedRouter("*Combat Off*");

        Assert.Empty(h.Events);
    }

    [Fact]
    public void DoesNotDoubleFire_OnSecondCombatOff()
    {
        // The exp is consumed on the first *Combat Off*, so a later non-death
        // *Combat Off* (a thrown-weapon bounce, a mid-spell interrupt) can't re-fire
        // a phantom death on the stale exp.
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedRouter("*Combat Off*");
        Assert.Single(h.Events);

        h.FeedRouter("*Combat Off*");
        Assert.Single(h.Events);
    }

    [Fact]
    public void CombatOffOutsideExpWindow_DoesNotFire()
    {
        // A *Combat Off* more than the 5s window after the exp isn't that kill's Off.
        using Harness h = new();
        DateTimeOffset clock = DateTimeOffset.UnixEpoch;
        h.Watcher.NowProvider = () => clock;

        h.FeedRouter("You gain 9 experience.");
        clock += TimeSpan.FromSeconds(6);
        h.FeedRouter("*Combat Off*");

        Assert.Empty(h.Events);
    }

    // Report paradigm-20261010-145330: a room spell killed three of four and printed no
    // *Combat Off* (the survivor kept it running). The attack then sent at the survivor
    // printed one ahead of its *Combat Engaged*, inside the window the exp lines had
    // opened, and was read as a fourth death.
    [Fact]
    public void CombatOffAnsweringAnEchoedAttack_IsNotADeath()
    {
        using Harness h = new();

        h.FeedRouter("You gain 16250 experience.");
        h.FeedRouter("You gain 16250 experience.");
        h.FeedRouter("You gain 16250 experience.");
        h.FeedEchoed("aslt brute zombie");
        h.FeedRouter("*Combat Off*");
        h.FeedRouter("*Combat Engaged*");

        Assert.Empty(h.Events);

        // The exp lines are spent: the buff cast next prints an Off of its own.
        h.FeedRouter("*Combat Off*");
        Assert.Empty(h.Events);
    }

    [Fact]
    public void CombatOffAnsweringATypedBreak_IsNotADeath()
    {
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedEchoed("break");
        h.FeedRouter("*Combat Off*");

        Assert.Empty(h.Events);
    }

    // The kill's own Off follows its exp line, with the command that led to it further
    // back, so it is still a death.
    [Fact]
    public void KillAfterAnEchoedAttack_StillFiresDeath()
    {
        using Harness h = new();

        h.FeedEchoed("aslt brute zombie");
        h.FeedRouter("*Combat Engaged*");
        h.FeedRouter("The brute zombie keels over like a hewn tree!");
        h.FeedRouter("You gain 16250 experience.");
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
    }

    // Text the game appends to a prompt row without clearing it reads as an echo. An
    // exp line that arrived that way must not turn its own Off into a command's.
    [Fact]
    public void ExpLineOnAPromptRow_ThenCombatOff_StillFiresDeath()
    {
        using Harness h = new();

        h.FeedEchoed("You gain 9 experience.");
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
    }

    // A statline the extractor can't split arrives as one line, prompt and command
    // together, so no echo is read and the Off keeps its old reading.
    [Fact]
    public void CommandOnAnUnsplitStatline_KeepsTheOldReading()
    {
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedRouter("<724hp 343ma> aslt brute zombie");
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
    }
}
