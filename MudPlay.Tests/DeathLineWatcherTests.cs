using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Health;
using MudPlay.Game.Map;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// DeathLineWatcher raises PlayerDied on "You have been killed!", the victim's own
// line for every death by damage (GAME_MECHANICS "Death lines & the miracle-save").
// No death line names the killer, and "You have been slain by <killer>." is not a
// game line.
public sealed class DeathLineWatcherTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public DeathLineWatcher Watcher { get; }
        public int Deaths { get; private set; }
        public List<LogEntry> LogEntries { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Watcher = new DeathLineWatcher(Router, Log);
            Watcher.PlayerDied += () => Deaths++;
            Log.EntryAdded += LogEntries.Add;
        }

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Dispose() => Watcher.Dispose();
    }

    [Fact]
    public void KilledLine_FiresPlayerDied()
    {
        using Harness h = new();
        h.Feed("You have been killed!");
        Assert.Equal(1, h.Deaths);
    }

    [Theory]
    [InlineData("You have been slain by a giant rat.")]   // not a game line
    [InlineData("Raijin is dead.")]                       // another player's death
    [InlineData("You have 7 lives left.")]                // the readout DeathDetector reads
    [InlineData("You now have 7 lives remaining.")]       // a suicide's readout
    [InlineData("Bob gossips: You have been killed!")]    // someone quoting it
    public void OtherLines_DoNotFire(string line)
    {
        using Harness h = new();
        h.Feed(line);
        Assert.Equal(0, h.Deaths);
    }

    [Fact]
    public void EmitsAWarnRow()
    {
        using Harness h = new();
        h.Feed("You have been killed!");
        Assert.Contains(h.LogEntries, e =>
            e.Source == DeathLineWatcher.LogCategory && e.Severity == LogSeverity.Warn);
    }

    [Fact]
    public void EachDeathFiresOnce()
    {
        using Harness h = new();
        h.Feed("You have been killed!");
        h.Feed("But, due to a miracle, you have been saved.");
        h.Feed("You have 7 lives left.");
        h.Feed("You have been killed!");
        Assert.Equal(2, h.Deaths);
    }

    [Fact]
    public void Dispose_StopsFurtherEmits()
    {
        Harness h = new();
        h.Feed("You have been killed!");
        h.Dispose();
        h.Feed("You have been killed!");
        Assert.Equal(1, h.Deaths);
    }

    // The bytes of a miracle-save death through the emulator and the line extractor,
    // read by both death signals as AppServices wires them: the line extractor feeds
    // the router (DeathLineWatcher) and DeathDetector (RoomTracker.PlayerDeathObserved).
    // Each fires once, and the death line comes first, ahead of the lives readout.
    [Fact]
    public void CapturedDeath_DeathLineFiresOnceAheadOfTheLivesReadout()
    {
        const string esc = "\u001b";
        TerminalEmulator emulator = new(80, 25);
        LineExtractor extractor = new(emulator);
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        extractor.LineEmitted += router.Dispatch;
        using DeathLineWatcher watcher = new(router);
        RoomTracker tracker = new(new RoomGraphManager(new GameDataCache()));
        using DeathDetector detector = new(tracker);
        detector.AttachLineExtractor(extractor);

        List<string> order = new();
        watcher.PlayerDied += () => order.Add("PlayerDied");
        tracker.PlayerDeathObserved += () => order.Add("PlayerDeathObserved");

        void Feed(string text) => emulator.Feed(Encoding.Latin1.GetBytes(text));
        Feed($"{esc}[0;37m[HP=-12/MA=40]:");
        Feed($"\r\n{esc}[1;31mYou have been killed!\r\n");
        Feed($"{esc}[0;37mBut, due to a miracle, you have been saved.\r\n");
        Feed($"{esc}[0;37mYou have 7 lives left.\r\n");
        Feed($"{esc}[1;36mTemple, Halls of the Dead\r\n");
        Feed($"{esc}[0;32mObvious exits: up\r\n");
        Feed($"{esc}[0;37m[HP=200/MA=40]:");

        Assert.Equal(["PlayerDied", "PlayerDeathObserved"], order);
    }

    // DeathFloorTracer is wired to PlayerDied (AppServices). The death line comes
    // before the respawn's full-HP prompt, so the HP the character bled to is still
    // the last one read when the floor is judged.
    [Fact]
    public void DeathLine_ReachesTheFloorTracerWithTheHpDiedAt()
    {
        using Harness h = new();
        PlayerState state = new() { MaxHp = 200 };
        RealmProfile realm = new() { Name = "TestRealm", PlayerDiesAtHp = -25 };
        using DeathFloorTracer tracer = new(state,
            () => (new BbsProfile { Name = "TestBBS", Realms = { realm } }, realm), _ => { });
        h.Watcher.PlayerDied += tracer.RecordDeath;

        foreach (int hp in new[] { 40, 0, -4, -8, -12, -16, -20 }) state.Hp = hp;
        h.Feed("You have been killed!");
        state.Hp = 200;   // the respawn's prompt

        Assert.Equal(-20, realm.PlayerDiesAtHp);
    }
}
