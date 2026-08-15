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
}
