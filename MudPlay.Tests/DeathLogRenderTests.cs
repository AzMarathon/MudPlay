using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// DeathRecoveryManager.RenderDeathLog — the plain-text format of a captured
// "How did I Die?" log: a short header (who / when / where / lives / death line)
// followed by the backscroll tail, oldest → newest.
public sealed class DeathLogRenderTests
{
    [Fact]
    public void RenderDeathLog_IncludesHeaderAndTranscript()
    {
        var record = new DeathRecord(
            DateTimeOffset.UnixEpoch, new RoomRef(3, 42), 5,
            "You have been killed!")
        {
            RecordNumber = 1,
            RoomName = "A dark alley",
        };

        var lines = new List<TranscriptSnapshot.Line>
        {
            new(DateTimeOffset.UnixEpoch, "the orc swings a rusty axe"),
            new(null, "You have been killed!"),
        };

        string text = DeathRecoveryManager.RenderDeathLog(record, lines, "Grushnak");

        Assert.Contains("Death log — Grushnak", text);
        Assert.Contains("A dark alley", text);
        Assert.Contains("3/42", text);
        Assert.Contains("Lives remaining: 5", text);
        Assert.Contains("Death line: You have been killed!", text);
        Assert.Contains("the orc swings a rusty axe", text);
    }

    [Fact]
    public void RenderDeathLog_OmitsDeathLine_WhenMessageBlank()
    {
        var record = new DeathRecord(DateTimeOffset.UnixEpoch, null, 0, null)
        {
            RecordNumber = 2,
        };

        var lines = new List<TranscriptSnapshot.Line> { new(null, "some final line") };

        string text = DeathRecoveryManager.RenderDeathLog(record, lines, "Nobody");

        Assert.DoesNotContain("Death line:", text);
        Assert.Contains("some final line", text);
    }
}
