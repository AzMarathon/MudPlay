using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Pins the blanket train-stats automation lockout: while the trainer / creation
// form owns the keyboard, TrainerScreenGate raises an
// EngineSendGate hold that no-ops every wrapped engine — even on a
// cursor-positioned realm whose "Point Cost Chart" marker never confirms — and
// releases it when the form closes. A raw (un-wrapped) sender still pierces the
// hold, mirroring how the auto-trainer's CP replay + user input stay live.
public sealed class TrainerScreenGateTests
{
    private static readonly DateTime Now = new(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

    private static (TrainerMenuTracker tracker, TrainerScreenGate gate, EngineSendGate send, MessageRouter router) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        TrainerMenuTracker tracker = new(router, state, now: () => Now, schedule: new ManualTimers().Arm);
        EngineSendGate send = new();
        TrainerScreenGate gate = new(tracker, send);
        return (tracker, gate, send, router);
    }

    private static void Dispatch(MessageRouter router, string text) =>
        router.Dispatch(new LineExtractor.EmittedLine(
            Text: text,
            Attributes: Array.Empty<CellAttributes>(),
            Timestamp: Now,
            IsPromptLine: false));

    // The game's answer to `train stats` (its echo, then the form), and the room
    // shown again once the form is left.
    private static void FormOpensThenCloses(TrainerMenuTracker tracker, MessageRouter router)
    {
        ServerFeed game = new(router);
        tracker.ReadScreen = () => TrainStatsWire.ScreenText(game.Emulator.Screen);
        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));
        game.Raw("\u001b[2J\u001b[H" + TrainStatsWire.RoomRedrawn());
    }

    [Fact]
    public void TrainStats_WithoutMarker_HoldsTheGate()
    {
        // The Paradigm case: the cursor-positioned stat box never emits its
        // "Point Cost Chart" marker inline, so IsInTrainerMenu stays false — but
        // the form still owns the keyboard, so the send gate must lock anyway.
        var (tracker, gate, send, _) = Setup();
        Assert.False(send.IsLocked);

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));

        Assert.True(gate.IsHeld);
        Assert.True(send.IsLocked);
        Assert.False(tracker.IsInTrainerMenu); // marker never confirmed
    }

    [Fact]
    public void WrappedEngineSend_NoOpsWhileHeld_ResumesAfter()
    {
        var (tracker, _, send, router) = Setup();
        List<byte[]> wire = new();
        Action<byte[]> engine = send.WrapEngineSender(wire.Add);

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        engine(Encoding.Latin1.GetBytes("par\r")); // would leak into Family Name
        Assert.Empty(wire);

        FormOpensThenCloses(tracker, router);

        engine(Encoding.Latin1.GetBytes("par\r"));
        Assert.Single(wire);
    }

    [Fact]
    public void RawSender_PiercesHold()
    {
        // The auto-trainer's CP replay + the user's manual input ride the raw
        // sender, not the wrapped one, so they must still send while held.
        var (tracker, _, send, _) = Setup();
        List<byte[]> wire = new();
        Action<byte[]> raw = wire.Add; // un-wrapped, like SendUserInput

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.True(send.IsLocked);

        raw(Encoding.Latin1.GetBytes("1\r"));
        Assert.Single(wire);
    }

    [Fact]
    public void MarkerConfirmedMenu_HoldsTheGate()
    {
        // Character creation / stock realm: confirmed via the marker with no
        // command-armed input session. MenuOwnsKeyboard folds it in, so the hold
        // must rise on this path too.
        var (tracker, gate, send, router) = Setup();
        Dispatch(router, "  MAJOR MUD Character Creation      Point Cost Chart");

        Assert.True(tracker.IsInTrainerMenu);
        Assert.True(gate.IsHeld);
        Assert.True(send.IsLocked);
    }

    [Fact]
    public void FormExit_ReleasesTheGate()
    {
        var (tracker, gate, send, router) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.True(send.IsLocked);

        FormOpensThenCloses(tracker, router);

        Assert.False(gate.IsHeld);
        Assert.False(send.IsLocked);
    }

    [Fact]
    public void ComposesWithOtherHolds_StaysLockedUntilBothClear()
    {
        // The trainer hold is named, so it stacks with (say) the suicide-password
        // hold rather than clobbering it — sends stay gated until every hold lifts.
        var (tracker, _, send, router) = Setup();
        send.Hold("password");

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        FormOpensThenCloses(tracker, router);   // trainer hold released here

        Assert.True(send.IsLocked); // password hold still up
        send.Release("password");
        Assert.False(send.IsLocked);
    }

    // The telepath pacer writes queued telepaths from its own timer, past the send
    // gate: one queued just before the form opened would be typed into it. The
    // gate holds the pacer over the same stretch, alongside any other holder.
    [Fact]
    public void TheTelepathPacer_IsHeldWhileTheFormOwnsTheKeyboard()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        TrainerMenuTracker tracker = new(router, new PartyState(), now: () => Now, schedule: new ManualTimers().Arm);
        ManualTimers pacerTimers = new();
        DateTimeOffset now = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
        TelepathPacer pacer = new(pacerTimers.Schedule, () => now);
        List<string> wire = new();
        pacer.SetWriter(b => wire.Add(Encoding.Latin1.GetString(b)));
        using TrainerScreenGate gate = new(tracker, new EngineSendGate(), telepaths: pacer);

        pacer.Send(Encoding.Latin1.GetBytes("/Ironbar {Trained to level 31.}\r/Barter {HP=533/533}\r"));
        Assert.Single(wire);                     // the second waits out the pacer's gap
        pacer.Hold("@suicide");
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.Equal("@suicide, TrainerMenu", pacer.HeldBy);

        now += TelepathPacer.Gap;
        pacerTimers.Advance(TelepathPacer.Gap);
        Assert.Single(wire);                     // held: nothing typed into the form

        FormOpensThenCloses(tracker, router);
        Assert.Equal("@suicide", pacer.HeldBy);  // the trainer let go; @suicide still holds
        pacer.Release("@suicide");
        pacerTimers.Advance(TelepathPacer.Gap);
        Assert.Equal(2, wire.Count);
    }

    [Fact]
    public void Dispose_ReleasesStuckHold()
    {
        var (tracker, gate, send, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.True(send.IsLocked);

        gate.Dispose();
        Assert.False(send.IsLocked);
    }
}
