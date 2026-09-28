using System.Text;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// A gear set that disagrees with our recorded alignment gets a `who` to verify it —
// on a prompt only, again whenever the answer may have changed, but a confirmed
// mismatch isn't re-checked every prompt.
public sealed class AlignmentGearCheckTests
{
    private DateTimeOffset _now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private (AlignmentGearCheck Check, List<byte[]> Sent) Build(bool mismatch = true, bool drifts = false)
    {
        AlignmentGearCheck check = new(() => mismatch, () => drifts, now: () => _now);
        List<byte[]> sent = new();
        check.SetWireSender(sent.Add);
        return (check, sent);
    }

    private static int Whos(List<byte[]> sent) =>
        sent.Count(b => Encoding.Latin1.GetString(b).TrimEnd('\r') == "who");

    [Fact]
    public void SendsOnlyOnAPrompt_AndOnlyWhenTheSetsDisagree()
    {
        var (check, sent) = Build();
        check.RequestCheck();
        Assert.Equal(0, Whos(sent));      // not in the game until a prompt

        check.OnPrompt();
        Assert.Equal(1, Whos(sent));

        var (quiet, quietSent) = Build(mismatch: false);
        quiet.RequestCheck();
        quiet.OnPrompt();
        Assert.Equal(0, Whos(quietSent));
    }

    [Fact]
    public void NewEvidence_ReChecksAfterAMinute_ButNotEveryPrompt()
    {
        var (check, sent) = Build();
        check.RequestCheck();
        check.OnPrompt();

        check.RequestCheck();             // e.g. the dark-cloud line
        check.OnPrompt();
        Assert.Equal(1, Whos(sent));      // too soon

        _now += TimeSpan.FromMinutes(1);
        check.OnPrompt();
        Assert.Equal(2, Whos(sent));
    }

    [Fact]
    public void ReapplyingASet_WaitsTheLongerGap()
    {
        var (check, sent) = Build();
        check.RequestCheck();
        check.OnPrompt();

        _now += TimeSpan.FromMinutes(2);
        check.RequestRoutineCheck();
        check.OnPrompt();
        Assert.Equal(1, Whos(sent));

        _now += TimeSpan.FromMinutes(8);
        check.OnPrompt();
        Assert.Equal(2, Whos(sent));
    }

    [Fact]
    public void ARealmThatDrifts_ReChecksOnATimer()
    {
        var (check, sent) = Build(drifts: true);
        check.OnPrompt();                 // first prompt: the timer is due
        Assert.Equal(1, Whos(sent));

        _now += TimeSpan.FromMinutes(5);
        check.OnPrompt();
        Assert.Equal(1, Whos(sent));

        _now += TimeSpan.FromMinutes(5);
        check.OnPrompt();
        Assert.Equal(2, Whos(sent));
    }
}
