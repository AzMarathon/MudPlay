using System.Text;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The wire sniffer for a hand-typed gear command, and the flag that tells a typed line
// from one the client sends for itself.
public sealed class OutboundGearObserverTests
{
    private static List<string> Observe(params string[] commands)
    {
        List<string> seen = new();
        OutboundGearObserver obs = new(seen.Add);
        foreach (string command in commands)
            obs.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r\n"));
        return seen;
    }

    // Report paradigm-20261009-122342 typed `Eq phoenix`. The short forms are the
    // game's own (GAME_MECHANICS "Command words and abbreviations").
    [Theory]
    [InlineData("Eq phoenix")]
    [InlineData("eq phoenix feather")]
    [InlineData("equip sword")]
    [InlineData("wear cloak")]
    [InlineData("wea cloak")]
    [InlineData("wield sword")]
    [InlineData("rem ring")]
    [InlineData("remove ring 2")]
    public void GearCommandWithAnItem_IsSeen(string command)
    {
        Assert.Equal(new[] { command }, Observe(command));
    }

    [Theory]
    [InlineData("eq")]               // nothing named: wears nothing
    [InlineData("rem ")]
    [InlineData("e")]                // east
    [InlineData("re ring")]          // `re` is no command
    [InlineData("wealth")]
    [InlineData("weal coins")]       // `weal`…`wealth` is wealth
    [InlineData("wiel sword")]       // `wield` has no short form on record
    [InlineData("rest")]
    [InlineData("get ring")]
    [InlineData("a giant rat")]
    public void AnythingElse_IsNot(string command)
    {
        Assert.Empty(Observe(command));
    }

    [Fact]
    public void WrappedSender_MarksItsSendsAsTheClientsOwn_TypedLinesAreNot()
    {
        EngineSendGate gate = new();
        List<bool> duringSend = new();
        void Wire(byte[] _) => duringSend.Add(gate.SendingClientCommand);
        Action<byte[]> engine = gate.WrapEngineSender(Wire);

        Wire(Encoding.Latin1.GetBytes("eq phoenix\r"));        // typed: straight to the wire
        engine(Encoding.Latin1.GetBytes("eq amber sceptre\r")); // the client's own
        gate.ReplayLastClientCommand();                         // a fumble's re-send of it
        Wire(Encoding.Latin1.GetBytes("rem ring\r"));

        Assert.Equal(new[] { false, true, true, false }, duringSend);
        Assert.False(gate.SendingClientCommand);
    }
}
