using System.Text;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

// The server acks telepaths in the order they were sent — "--- Telepath Sent to X
// ---" delivered, "--- Telepath Not Sent ---" refused by its throttle — so the pacer
// spaces them out and resends the refused ones.
public sealed class TelepathPacerTests
{
    private sealed class Harness
    {
        public DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        public readonly List<string> Wire = new();
        private readonly List<(DateTimeOffset Due, Action Action)> _timers = new();
        public readonly TelepathPacer Pacer;

        public Harness()
        {
            Pacer = new TelepathPacer((delay, action) => _timers.Add((Now + delay, action)), () => Now);
            Pacer.SetWriter(b => Wire.Add(Encoding.Latin1.GetString(b)));
        }

        public void Send(string text) => Pacer.Send(Encoding.Latin1.GetBytes(text));

        // Advance the clock, firing timers that come due along the way.
        public void Advance(TimeSpan by)
        {
            DateTimeOffset end = Now + by;
            while (true)
            {
                var due = _timers.Where(t => t.Due <= end).OrderBy(t => t.Due).FirstOrDefault();
                if (due.Action is null) break;
                _timers.Remove(due);
                Now = due.Due;
                due.Action();
            }
            Now = end;
        }
    }

    [Fact]
    public void Telepaths_AreSpacedAtLeastTheGapApart_OtherLinesGoStraightThrough()
    {
        Harness h = new();
        h.Send("/Raijin @health\r/Raijin @level\rstat\r/Raijin @version\r");

        // `stat` isn't held behind the telepaths; only the first telepath goes now.
        Assert.Equal(new[] { "stat\r", "/Raijin @health\r" }, h.Wire);

        h.Advance(TimeSpan.FromMilliseconds(99));
        Assert.Equal(2, h.Wire.Count);
        h.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("/Raijin @level\r", h.Wire[^1]);
        h.Advance(TelepathPacer.Gap);
        Assert.Equal("/Raijin @version\r", h.Wire[^1]);
    }

    // Three sent, then "Sent to", "Not Sent", "Not Sent": the 2nd and 3rd are resent,
    // in their original order.
    [Fact]
    public void NotSent_ResendsTheRefusedTelepaths_InOrder()
    {
        Harness h = new();
        h.Send("/Raijin @health\r/Raijin @level\r/Raijin @version\r");
        h.Advance(TimeSpan.FromMilliseconds(300));
        h.Wire.Clear();

        h.Pacer.OnAck("[HP=32/MA=27]:--- Telepath Sent to Raijin ---");
        h.Pacer.OnAck("--- Telepath Not Sent ---");
        h.Pacer.OnAck("[HP=32/MA=27]:--- Telepath Not Sent ---");
        h.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(new[] { "/Raijin @level\r", "/Raijin @version\r" }, h.Wire);
        Assert.Equal(2, h.Pacer.Resends);
    }

    [Fact]
    public void NotSent_GivesUpAfterMaxAttempts()
    {
        Harness h = new();
        h.Send("/Raijin @level\r");
        for (int i = 0; i < TelepathPacer.MaxAttempts; i++)
        {
            h.Advance(TimeSpan.FromMilliseconds(200));
            h.Pacer.OnAck("--- Telepath Not Sent ---");
        }
        h.Advance(TimeSpan.FromMilliseconds(200));

        Assert.Equal(TelepathPacer.MaxAttempts, h.Wire.Count);
        Assert.Equal(1, h.Pacer.GivenUp);
    }

    // A chat line quoting an ack must not trigger a resend; "/raij" is acked as Raijin.
    [Fact]
    public void Acks_AreAnchored_AndMatchAnAbbreviatedTarget()
    {
        Harness h = new();
        h.Send("/raij @exp\r");
        h.Pacer.OnAck("Bob gossips: --- Telepath Not Sent ---");
        Assert.Equal(1, h.Pacer.InFlight);

        h.Pacer.OnAck("--- Telepath Sent to Raijin ---");
        Assert.Equal(0, h.Pacer.InFlight);
        Assert.Equal(0, h.Pacer.Resends);
    }

    [Theory]
    [InlineData("/Raijin @level\r", "Raijin")]
    [InlineData("/raij hi there\r", "raij")]
    [InlineData("say /Raijin\r", null)]
    [InlineData("/ nothing\r", null)]
    public void TelepathTarget_ReadsTheSlashForm(string line, string? target) =>
        Assert.Equal(target, TelepathPacer.TelepathTarget(Encoding.Latin1.GetBytes(line)));
}
