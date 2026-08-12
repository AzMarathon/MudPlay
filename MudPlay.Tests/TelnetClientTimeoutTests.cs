using MudPlay.Net;
using Xunit;

namespace MudPlay.Tests;

// TCP_USER_TIMEOUT schedule math — bounds how long a dead socket goes unnoticed
// so a lost carrier can't take the OS retransmit default (~13 min) to surface.
public sealed class TelnetClientTimeoutTests
{
    [Fact]
    public void DeadConnectionCapMs_UsesIdlePlusKeepaliveTail_WhenConfigured()
    {
        // idle + 30s tail, in milliseconds — matches the keepalive probe schedule.
        Assert.Equal(60_000u, TelnetClient.DeadConnectionCapMs(30));
        Assert.Equal(120_000u, TelnetClient.DeadConnectionCapMs(90));
    }

    [Fact]
    public void DeadConnectionCapMs_FallsBackToStandaloneCap_WhenUnset()
    {
        // No keepalive configured (0 / negative) → the standalone 60s cap still
        // bounds the dead-connection detection instead of the OS default.
        Assert.Equal(60_000u, TelnetClient.DeadConnectionCapMs(0));
        Assert.Equal(60_000u, TelnetClient.DeadConnectionCapMs(-5));
    }

    [Fact]
    public void DeadConnectionCapMs_ClampsAbsurdIdle_ToUintRange()
    {
        // An absurd idle must not overflow the raw uint socket-option value.
        Assert.Equal(uint.MaxValue, TelnetClient.DeadConnectionCapMs(int.MaxValue));
    }
}
