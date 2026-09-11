using System.Text;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// <see cref="EngineSendGate"/> named-hold composition — more than one flow
/// (suicide-password entry, mortally-wounded drop) can gate engine sends at
/// once, so the gate stays locked until EVERY hold releases.
/// </summary>
public sealed class EngineSendGateTests
{
    [Fact]
    public void NoHolds_IsUnlocked()
    {
        EngineSendGate gate = new();
        Assert.False(gate.IsLocked);
    }

    [Fact]
    public void SingleHold_LocksUntilReleased()
    {
        EngineSendGate gate = new();
        gate.Hold("SuicidePassword");
        Assert.True(gate.IsLocked);
        gate.Release("SuicidePassword");
        Assert.False(gate.IsLocked);
    }

    [Fact]
    public void TwoHolds_StayLockedUntilBothRelease()
    {
        // The R7 case: a drop raises "MortallyWounded" while a suicide-password
        // flow already holds "SuicidePassword". Releasing one must NOT unlock —
        // the other flow still needs the gate down.
        EngineSendGate gate = new();
        gate.Hold("SuicidePassword");
        gate.Hold("MortallyWounded");
        Assert.True(gate.IsLocked);

        gate.Release("SuicidePassword");
        Assert.True(gate.IsLocked);   // MortallyWounded still holds

        gate.Release("MortallyWounded");
        Assert.False(gate.IsLocked);
    }

    [Fact]
    public void Hold_IsIdempotentPerReason()
    {
        // Re-asserting the same reason must not require a matching double-release.
        EngineSendGate gate = new();
        gate.Hold("MortallyWounded");
        gate.Hold("MortallyWounded");
        gate.Release("MortallyWounded");
        Assert.False(gate.IsLocked);
    }

    [Fact]
    public void Release_UnknownReason_IsNoOp()
    {
        EngineSendGate gate = new();
        gate.Hold("MortallyWounded");
        gate.Release("NeverHeld");
        Assert.True(gate.IsLocked);
    }

    [Fact]
    public void Wrapper_ShortCircuitsWhileAnyHold()
    {
        EngineSendGate gate = new();
        List<byte[]> sent = new();
        Action<byte[]> wrapped = gate.WrapEngineSender(sent.Add);

        wrapped(Encoding.Latin1.GetBytes("par\r"));
        Assert.Single(sent);

        gate.Hold("MortallyWounded");
        wrapped(Encoding.Latin1.GetBytes("par\r"));
        Assert.Single(sent); // dropped on the floor

        gate.Release("MortallyWounded");
        wrapped(Encoding.Latin1.GetBytes("par\r"));
        Assert.Equal(2, sent.Count);
    }

    // ----- confusion-fumble replay of the last CLIENT command ----------

    private static string Last(List<byte[]> sent) => Encoding.Latin1.GetString(sent[^1]);

    [Fact]
    public void Replay_ResendsLastClientCommand()
    {
        // A fumble ate the last client command; re-sending it performs the action.
        EngineSendGate gate = new();
        List<byte[]> sent = new();
        Action<byte[]> wrapped = gate.WrapEngineSender(sent.Add);

        wrapped(Encoding.Latin1.GetBytes("cast mist dragon\r"));
        Assert.Single(sent);

        gate.ReplayLastClientCommand();
        Assert.Equal(2, sent.Count);
        Assert.Equal("cast mist dragon\r", Last(sent));
    }

    [Fact]
    public void Replay_ResendsItemUse()
    {
        // Item-use is a multi-token command, so it re-fires (unlike a bare move).
        EngineSendGate gate = new();
        List<byte[]> sent = new();
        Action<byte[]> wrapped = gate.WrapEngineSender(sent.Add);

        wrapped(Encoding.Latin1.GetBytes("use waterskin\r"));
        gate.ReplayLastClientCommand();

        Assert.Equal(2, sent.Count);
        Assert.Equal("use waterskin\r", Last(sent));
    }

    [Theory]
    [InlineData("n\r")]
    [InlineData("ne\r")]
    [InlineData("up\r")]
    [InlineData("South\r")]
    public void Replay_SkipsBareMovement(string move)
    {
        // A fumbled move is recovered by the move-revert + walker re-send, so the
        // gate must NOT also re-fire it — a second step would desync position.
        EngineSendGate gate = new();
        List<byte[]> sent = new();
        Action<byte[]> wrapped = gate.WrapEngineSender(sent.Add);

        wrapped(Encoding.Latin1.GetBytes(move));
        gate.ReplayLastClientCommand();

        Assert.Single(sent);   // not re-fired
    }

    [Fact]
    public void Replay_NoOpWhileHeld()
    {
        EngineSendGate gate = new();
        List<byte[]> sent = new();
        Action<byte[]> wrapped = gate.WrapEngineSender(sent.Add);
        wrapped(Encoding.Latin1.GetBytes("a orc\r"));

        gate.Hold("MortallyWounded");
        gate.ReplayLastClientCommand();
        Assert.Single(sent);   // held → no replay
    }

    [Fact]
    public void Replay_NoOpBeforeAnyClientSend()
    {
        // Nothing sent yet (e.g. the only traffic was user-typed, which bypasses the
        // gate) → nothing to replay.
        EngineSendGate gate = new();
        gate.ReplayLastClientCommand();   // must not throw
    }
}
