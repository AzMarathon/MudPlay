using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The reconnect-resume releaser: a disconnect can strand the Acquisition gate's
// deferred-collect hold, pausing the loop until a manual `rm`. Armed on reconnect,
// it drops the hold on the first in-game prompt so the loop resumes.
public sealed class DeferredCollectReconnectReleaserTests
{
    // Minimal MajorMUD status line — drives WirePromptScanner.PromptObserved.
    private static readonly byte[] PromptBytes = Encoding.Latin1.GetBytes("[HP=100]: ");

    [Fact]
    public void UnarmedPrompt_DoesNotRelease()
    {
        WirePromptScanner scanner = new();
        int fired = 0;
        using var r = new DeferredCollectReconnectReleaser(scanner, () => fired++);

        scanner.Append(PromptBytes);   // in-game prompt, but not armed (first connect)

        Assert.Equal(0, fired);
    }

    [Fact]
    public void ArmedPrompt_ReleasesOnceOnFirstPrompt()
    {
        WirePromptScanner scanner = new();
        int fired = 0;
        using var r = new DeferredCollectReconnectReleaser(scanner, () => fired++);

        r.Arm();                       // reconnect
        scanner.Append(PromptBytes);   // first in-game prompt after reconnect → release
        Assert.Equal(1, fired);

        // One-shot: a later prompt in the same session doesn't re-release.
        scanner.Append(PromptBytes);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void ReArm_FiresAgainOnNextReconnect()
    {
        WirePromptScanner scanner = new();
        int fired = 0;
        using var r = new DeferredCollectReconnectReleaser(scanner, () => fired++);

        r.Arm();
        scanner.Append(PromptBytes);
        Assert.Equal(1, fired);

        r.Arm();                       // a second reconnect re-arms
        scanner.Append(PromptBytes);
        Assert.Equal(2, fired);
    }
}
