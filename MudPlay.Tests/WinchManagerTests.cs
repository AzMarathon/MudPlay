using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// FSM coverage for WinchManager. Drives the manager with fabricated winch result
// lines via MessageRouter.Dispatch and a controllable stand-in for the UI-thread
// one-shot, inspecting the wire bytes + terminal callback result.
public sealed class WinchManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; }
        public WinchManager Mgr { get; }
        public List<byte[]> Sent { get; } = new();
        public bool GateOpen { get; set; }

        private Action? _pending;

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Mgr = new WinchManager(Router, isGateOpen: _ => GateOpen, scheduleDelay: Schedule);
            Mgr.SetWireSender(Sent.Add);
        }

        // Captures the latest scheduled follow-up (re-pull / gate poll / watchdog);
        // Fire runs it. Never auto-fires, so line-driven transitions are unaffected.
        private IDisposable Schedule(TimeSpan _, Action callback)
        {
            _pending = callback;
            return new FakeHandle(() => { if (ReferenceEquals(_pending, callback)) _pending = null; });
        }

        public bool HasPending => _pending is not null;
        public void Fire() => _pending?.Invoke();

        public void Line(string text) =>
            Router.Dispatch(new LineExtractor.EmittedLine(text, [], DateTimeOffset.UtcNow, false));

        public IReadOnlyList<string> AllSent =>
            Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public int Count(string cmd) => AllSent.Count(c => c == cmd);

        public void Dispose() => Mgr.Dispose();

        private sealed class FakeHandle(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private const string TurnLine = "You heave mightily on the winch, and it begins to turn!";
    private const string BudgeLine = "You heave mightily on the winch, but it does not budge.";

    [Fact]
    public void Pull_Turns_GateAlreadyOpen_ReportsTurnedImmediately()
    {
        using Harness h = new() { GateOpen = true };
        WinchResult? result = null;
        h.Mgr.Enqueue(Direction.W, "pull winch", waitForGate: true, "walker", r => result = r);

        Assert.Equal("pull winch", h.AllSent[0]);
        h.Line(TurnLine);

        Assert.IsType<WinchResult.Turned>(result);
        Assert.Equal(0, h.Count("l"));   // gate already open — no poll look needed
    }

    [Fact]
    public void Pull_Turns_GateOpensAfterPoll_ReportsTurned()
    {
        using Harness h = new() { GateOpen = false };
        WinchResult? result = null;
        h.Mgr.Enqueue(Direction.W, "pull winch", waitForGate: true, "walker", r => result = r);

        h.Line(TurnLine);                  // turned, but gate not open yet → poll
        Assert.Null(result);
        Assert.Equal(1, h.Count("l"));     // forced a room re-display
        Assert.True(h.HasPending);

        h.GateOpen = true;                 // the re-display showed the gate open
        h.Fire();                          // next poll checks the gate

        Assert.IsType<WinchResult.Turned>(result);
    }

    [Fact]
    public void Pull_WontBudge_Retries_ThenTurns()
    {
        using Harness h = new() { GateOpen = true };
        WinchResult? result = null;
        h.Mgr.Enqueue(Direction.W, "pull winch", waitForGate: true, "walker", r => result = r);

        h.Line(BudgeLine);                 // first pull didn't budge → schedule re-pull
        Assert.Null(result);
        h.Fire();                          // the paced re-pull
        Assert.Equal(2, h.Count("pull winch"));

        h.Line(TurnLine);                  // second pull turned it; gate already open
        Assert.IsType<WinchResult.Turned>(result);
    }

    [Fact]
    public void Pull_WontBudge_ExhaustsCap_Fails()
    {
        using Harness h = new();
        WinchResult? result = null;
        h.Mgr.Enqueue(Direction.W, "pull winch", waitForGate: true, "walker", r => result = r);

        // Keep refusing: each budge either schedules a re-pull (fire it) or, once the
        // attempt cap is hit, fails. Bounded loop well past the cap.
        for (int i = 0; i < 20 && result is null; i++)
        {
            h.Line(BudgeLine);
            if (result is null && h.HasPending) h.Fire();
        }

        Assert.IsType<WinchResult.Failed>(result);
    }

    [Fact]
    public void Turned_GatePollExhausts_Fails()
    {
        using Harness h = new() { GateOpen = false };   // gate never opens
        WinchResult? result = null;
        h.Mgr.Enqueue(Direction.W, "pull winch", waitForGate: true, "walker", r => result = r);

        h.Line(TurnLine);
        for (int i = 0; i < 20 && result is null && h.HasPending; i++) h.Fire();

        Assert.IsType<WinchResult.Failed>(result);
    }

    [Fact]
    public void PullOnly_Turns_ReportsTurnedWithoutPollingGate()
    {
        // Cross-room detour: no gate in this room to poll — report Turned the instant
        // it begins to turn (retrying "does not budge" first), never sending a look.
        using Harness h = new() { GateOpen = false };
        WinchResult? result = null;
        h.Mgr.Enqueue(Direction.W, "pull winch", waitForGate: false, "loop", r => result = r);

        h.Line(BudgeLine);                 // strength roll missed → re-pull
        h.Fire();
        Assert.Equal(2, h.Count("pull winch"));
        h.Line(TurnLine);                  // turned

        Assert.IsType<WinchResult.Turned>(result);
        Assert.Equal(0, h.Count("l"));     // pull-only never polls the gate
    }

    [Fact]
    public void IsWinchExit_MatchesPullWinchMultiAction_NotAPlainExit()
    {
        RoomExit plain = new(new RoomKey(1, 2), RoomExitHint.None, null);
        Assert.False(WinchManager.IsWinchExit(plain));

        var winchData = new MultiActionExitData(
            RequiredActionCount: 1, RequiresSpecificOrder: false,
            Actions: new[] { new ExitAction(1, new[] { "pull winch" }, null) });
        RoomExit winch = new(new RoomKey(1, 3), RoomExitHint.MultiActionHidden, null,
            MultiAction: winchData);
        Assert.True(WinchManager.IsWinchExit(winch));
        Assert.Equal("pull winch", WinchManager.PullCommand(winch));
    }
}
