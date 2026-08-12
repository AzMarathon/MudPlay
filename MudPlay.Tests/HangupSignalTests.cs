using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Pins the two-flag one-shot semantics. Each ConsumeXxx returns true
/// exactly once per SignalHangup and resets back to false, so the
/// signal can be raised on subsequent hangups without leaking state.
/// </summary>
public sealed class HangupSignalTests
{
    [Fact]
    public void Initial_BothFlagsFalse()
    {
        HangupSignal s = new();
        Assert.False(s.ConsumeDisconnectIntent());
        Assert.False(s.ConsumeSuppressEntry());
    }

    [Fact]
    public void SignalHangup_ArmsBothFlags()
    {
        HangupSignal s = new();
        s.SignalHangup();
        var (disc, supp) = s.PeekForTests();
        Assert.True(disc);
        Assert.True(supp);
    }

    [Fact]
    public void ConsumeDisconnectIntent_OneShot()
    {
        HangupSignal s = new();
        s.SignalHangup();
        Assert.True(s.ConsumeDisconnectIntent());
        Assert.False(s.ConsumeDisconnectIntent());
    }

    [Fact]
    public void ConsumeSuppressEntry_OneShot()
    {
        HangupSignal s = new();
        s.SignalHangup();
        Assert.True(s.ConsumeSuppressEntry());
        Assert.False(s.ConsumeSuppressEntry());
    }

    [Fact]
    public void ConsumeMethods_Independent()
    {
        // Consuming one flag must NOT consume the other — they fire at
        // different moments (disconnect handler vs entry-arm time) so
        // each consumer reads only its own flag.
        HangupSignal s = new();
        s.SignalHangup();
        Assert.True(s.ConsumeDisconnectIntent());
        Assert.True(s.ConsumeSuppressEntry());
    }

    [Fact]
    public void SignalHangup_AfterPartialConsume_ReArmsBoth()
    {
        // A second hangup later in the session re-arms cleanly.
        HangupSignal s = new();
        s.SignalHangup();
        Assert.True(s.ConsumeDisconnectIntent());
        // Disconnect-intent already consumed, suppress-entry still pending.
        s.SignalHangup();
        Assert.True(s.ConsumeDisconnectIntent());
        Assert.True(s.ConsumeSuppressEntry());
    }

    [Fact]
    public void AllowNextEntry_ClearsStaleSuppress_SoReconnectAutoEnters()
    {
        // A deliberate hangup armed suppress-entry, but it was never consumed
        // (the manual reconnect's login walk didn't reach the entry latch).
        // Then an involuntary host-side drop happens: AllowNextEntry clears the
        // stale flag so the reconnect's Arm() sees it false and auto-enters.
        HangupSignal s = new();
        s.SignalHangup();
        s.AllowNextEntry();
        Assert.False(s.ConsumeSuppressEntry());
    }

    [Fact]
    public void AllowNextEntry_LeavesDisconnectIntentUntouched()
    {
        // AllowNextEntry only governs the entry latch; it must not disturb the
        // disconnect-intent flag (consumed earlier, in the Disconnected handler).
        HangupSignal s = new();
        s.SignalHangup();
        s.AllowNextEntry();
        Assert.True(s.ConsumeDisconnectIntent());
    }
}
