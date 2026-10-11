using System.Text;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The sent-command trail: one entry per command, with its origin, its sender and
// what became of it; nothing kept while a login or a password may be going out.
public sealed class SentCommandTrailTests
{
    private static byte[] Line(string text) => Encoding.Latin1.GetBytes(text + "\r");

    private static (EngineSendGate gate, SentCommandTrail trail, List<string> wire) Setup()
    {
        EngineSendGate gate = new();
        SentCommandTrail trail = new();
        gate.Trail = trail;
        return (gate, trail, new List<string>());
    }

    [Fact]
    public void WrappedSend_IsRecordedOnceWithItsSenderName()
    {
        var (gate, trail, wire) = Setup();
        Action<byte[]> engineSend = gate.WrapEngineSender(b => wire.Add(Encoding.Latin1.GetString(b)));
        Action<byte[]> combat = gate.Named(engineSend, "Combat");

        combat(Line("a orc"));

        SentCommandTrail.Entry entry = Assert.Single(trail.Snapshot());
        Assert.Equal("a orc", entry.Text);
        Assert.Equal(SentCommandTrail.Origin.Engine, entry.Origin);
        Assert.Equal("Combat", entry.Sender);
        Assert.Equal(SentCommandTrail.Outcome.Sent, entry.Outcome);
        Assert.Equal("a orc\r", Assert.Single(wire));
        Assert.Null(gate.CurrentSender);
    }

    [Fact]
    public void RouteStepsAndUserRelayedCommands_CarryTheirOrigin()
    {
        var (gate, trail, _) = Setup();
        Action<byte[]> route = gate.Named(gate.WrapEngineSender(_ => { }, routeSteps: true), "Walker");
        Action<byte[]> trigger = gate.Named(gate.MarkUserCommands(gate.WrapEngineSender(_ => { })), "Trigger");

        route(Line("n"));
        trigger(Line("gos hello"));

        SentCommandTrail.Entry[] entries = trail.Snapshot();
        Assert.Equal((SentCommandTrail.Origin.RouteStep, "Walker"), (entries[0].Origin, entries[0].Sender));
        Assert.Equal((SentCommandTrail.Origin.UserRelayed, "Trigger"), (entries[1].Origin, entries[1].Sender));
    }

    [Fact]
    public void LockedGate_RecordsTheDropWithTheHoldAndCountsIt()
    {
        var (gate, trail, wire) = Setup();
        Action<byte[]> poller = gate.Named(gate.WrapEngineSender(b => wire.Add(Encoding.Latin1.GetString(b))), "PartyPoller");
        gate.Hold("TrainerScreen");

        poller(Line("par"));

        Assert.Empty(wire);
        SentCommandTrail.Entry entry = Assert.Single(trail.Snapshot());
        Assert.Equal(SentCommandTrail.Outcome.Dropped, entry.Outcome);
        Assert.Equal("send gate held: TrainerScreen", entry.Why);
        Assert.Equal(1, gate.DroppedCount);
        Assert.Equal(("par", "PartyPoller"), (gate.LastDropped!.Value.Command, gate.LastDropped.Value.Sender));
        Assert.Equal(new[] { "TrainerScreen" }, gate.Holds);
    }

    [Fact]
    public void TakenForLaterBySneakKeeping_IsRecordedAsHeld()
    {
        var (gate, trail, wire) = Setup();
        gate.SetSneakHooks(takeForLater: command => command.StartsWith("say", StringComparison.Ordinal), sent: _ => { });
        Action<byte[]> sender = gate.WrapEngineSender(b => wire.Add(Encoding.Latin1.GetString(b)));

        sender(Line("say level up"));

        Assert.Empty(wire);
        SentCommandTrail.Entry entry = Assert.Single(trail.Snapshot());
        Assert.Equal(SentCommandTrail.Outcome.HeldForLater, entry.Outcome);
    }

    [Fact]
    public void TypedInput_BecomesOneEntryPerLine_WhetherSentWholeOrKeyByKey()
    {
        SentCommandTrail trail = new();

        trail.ObserveTyped(Encoding.Latin1.GetBytes("look\r\n"));
        foreach (char key in "nw") trail.ObserveTyped([(byte)key]);
        trail.ObserveTyped([0x08]);                       // backspace over the w
        trail.ObserveTyped([0x1B, (byte)'[', (byte)'A']); // an arrow key's escape is not text
        trail.ObserveTyped([(byte)'\r']);

        SentCommandTrail.Entry[] entries = trail.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.Equal("look", entries[0].Text);
        Assert.Equal("n[A", entries[1].Text);
        Assert.All(entries, e => Assert.Equal(SentCommandTrail.Origin.Typed, e.Origin));
    }

    [Fact]
    public void Withheld_KeepsNothingAndOnlyCounts()
    {
        bool withhold = true;
        SentCommandTrail trail = new() { Withhold = () => withhold };

        trail.ObserveTyped(Encoding.Latin1.GetBytes("my-login-name\r"));
        trail.Record("hunter2", SentCommandTrail.Origin.Engine, "Suicide", SentCommandTrail.Outcome.Sent);
        withhold = false;
        trail.Record("look", SentCommandTrail.Origin.Typed, null, SentCommandTrail.Outcome.Sent);

        SentCommandTrail.Entry entry = Assert.Single(trail.Snapshot());
        Assert.Equal("look", entry.Text);
        Assert.Equal(2, trail.Withheld);
    }

    [Fact]
    public void Ring_KeepsTheNewestCapacityEntriesInOrder()
    {
        SentCommandTrail trail = new();
        for (int i = 0; i < SentCommandTrail.Capacity + 10; i++)
            trail.Record($"c{i}", SentCommandTrail.Origin.Engine, "E", SentCommandTrail.Outcome.Sent);

        SentCommandTrail.Entry[] entries = trail.Snapshot();
        Assert.Equal(SentCommandTrail.Capacity, entries.Length);
        Assert.Equal("c10", entries[0].Text);
        Assert.Equal($"c{SentCommandTrail.Capacity + 9}", entries[^1].Text);
    }

    [Fact]
    public void ReportSection_PrintsOriginOutcomeAndReasonAndCensorsOnlyChatCommands()
    {
        var (gate, trail, _) = Setup();
        BugReportChatCensor censor = new BugReportChatCensor(Array.Empty<ulong>()).WithWords("flibber");
        gate.Named(gate.WrapEngineSender(_ => { }), "Combat")(Line("a flibber"));
        trail.ObserveTyped(Encoding.Latin1.GetBytes("gos you flibber\r"));
        gate.Hold("SuicidePassword");
        gate.Named(gate.WrapEngineSender(_ => { }), "PartyPoller")(Line("par"));

        BugReportBuilder.Section section = BugReportSentCommands.Build(trail, censor);

        Assert.Equal(3, section.Entries!.Count);
        Assert.Matches(@"^\d\d:\d\d:\d\d\.\d\d\d  engine Combat +sent     a flibber$", section.Entries[0]);
        Assert.Matches(@"  typed +sent     gos you \[censored\]$", section.Entries[1]);
        Assert.EndsWith("DROPPED  par    (send gate held: SuicidePassword)", section.Entries[2]);
    }
}
