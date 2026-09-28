using System.Text;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// Asking the game for our alignment: only on a prompt, when the game says it moved
// or a gear set disagrees with the record — no timers — and not more than once a
// minute (gear is stripped one line per item).
public sealed class AlignmentGearCheckTests
{
    private DateTimeOffset _now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private (AlignmentGearCheck Check, List<byte[]> Sent) Build(bool mismatch = true, string command = "who")
    {
        AlignmentGearCheck check = new(() => mismatch, () => command, now: () => _now);
        List<byte[]> sent = new();
        check.SetWireSender(sent.Add);
        return (check, sent);
    }

    private static List<string> Lines(List<byte[]> sent) =>
        sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

    [Fact]
    public void AMismatch_ChecksOnTheNextPrompt_OnlyWhileTheSetsDisagree()
    {
        var (check, sent) = Build();
        check.RequestCheck();
        Assert.Empty(sent);               // not in the game until a prompt
        check.OnPrompt();
        Assert.Equal(new[] { "who" }, Lines(sent));

        var (quiet, quietSent) = Build(mismatch: false);
        quiet.RequestCheck();
        quiet.OnPrompt();
        Assert.Empty(quietSent);
    }

    [Fact]
    public void TheGameSayingAlignmentMoved_ChecksEvenWhenTheSetsAgree()
    {
        var (check, sent) = Build(mismatch: false, command: "pro");
        check.RequestVerify();            // "Your … has been removed." / a refusal / a forgive
        check.OnPrompt();
        Assert.Equal(new[] { "pro" }, Lines(sent));
    }

    [Fact]
    public void NoTimers_AndAtMostOneCheckAMinute()
    {
        var (check, sent) = Build();
        check.OnPrompt();
        Assert.Empty(sent);               // nothing asked for

        check.RequestVerify();
        check.OnPrompt();
        check.RequestVerify();            // the next stripped item
        check.OnPrompt();
        Assert.Single(sent);

        _now += TimeSpan.FromMinutes(1);
        check.OnPrompt();
        Assert.Equal(2, sent.Count);      // the held one goes out

        _now += TimeSpan.FromHours(2);
        check.OnPrompt();
        Assert.Equal(2, sent.Count);      // and nothing on its own after that
    }
}
