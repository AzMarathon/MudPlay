using System.Text.Json;
using MudPlay.Game.Gmcp;
using MudPlay.Net;
using Xunit;

namespace MudPlay.Tests;

public sealed class GmcpStateTests
{
    internal static IEnumerable<JsonElement> Frames(string scenario)
    {
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Gmcp", "Recordings", scenario + ".jsonl")).Skip(1))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    internal static void Replay(GmcpState state, string scenario)
    {
        foreach (JsonElement frame in Frames(scenario))
        {
            if (frame.GetProperty("dir").GetString() != "s2c") continue;
            if (frame.GetProperty("kind").GetString() == "negotiation" && frame.GetProperty("telnet").GetString() == "IAC WONT GMCP") state.Disconnected();
            if (frame.GetProperty("kind").GetString() == "gmcp") state.Receive(new(frame.GetProperty("pkg").GetString()!, frame.GetProperty("msg").Clone()));
        }
    }

    internal static void Send(GmcpState state, string package, object payload) => state.Receive(new(package, JsonSerializer.SerializeToElement(payload)));

    [Theory]
    [InlineData("01-handshake")]
    [InlineData("02-walk")]
    [InlineData("03-fight")]
    [InlineData("04-leave-and-return")]
    [InlineData("05-wander-out-and-back")]
    [InlineData("06-resubscribe")]
    [InlineData("07-recovery-request")]
    [InlineData("08-death-and-respawn")]
    [InlineData("09-reconnect")]
    [InlineData("10-gmcp-off-midsession")]
    public void ContractSequences_ReconstructCompleteLiveState(string scenario)
    {
        GmcpState state = new();
        List<string> errors = new(); state.Diagnostic += errors.Add;
        Replay(state, scenario);
        Assert.Empty(errors);
        Assert.True(state.Compatible);
        Assert.True(state.InSession);
        Assert.NotNull(state.CurrentRoom);
        Assert.True(state.RosterComplete);
        foreach (long uid in state.LiveMonsters.Keys) Assert.True(state.StaticMonsters.ContainsKey(uid));
    }

    [Fact]
    public void RoomReset_KeepsStaticInfoButClearsLiveStateAndRejectsOldRoomUpdates()
    {
        GmcpState state = new(); Replay(state, "04-leave-and-return");
        long uid = Assert.Single(state.LiveMonsters).Key;
        JsonElement original = state.LiveMonsters[uid];
        Send(state, "Anomaly.Session.Reset", new { scope = "room", room = "1,999" });
        Assert.Empty(state.LiveMonsters);
        Assert.True(state.StaticMonsters.ContainsKey(uid));
        state.Receive(new("Anomaly.Mob.Update", original));
        Assert.Empty(state.LiveMonsters);
        Assert.Null(state.Snapshot("Anomaly.Room.Occupants"));
    }

    [Fact]
    public void MembershipRemoval_DiscardsLiveStateEvenBeforeTheGoneEvent()
    {
        GmcpState state = new(); Replay(state, "07-recovery-request");
        long uid = state.LiveMonsters.First().Key;
        Send(state, "Anomaly.Room.Occupants", new { room = state.CurrentRoom, mobs = Array.Empty<object>(), players = Array.Empty<object>() });
        Assert.False(state.LiveMonsters.ContainsKey(uid));
        Assert.True(state.StaticMonsters.ContainsKey(uid));
    }

    [Fact]
    public void GoneLeft_KeepsIdentityWhileDeathEvictsIt()
    {
        GmcpState state = new(); Replay(state, "05-wander-out-and-back");
        long uid = state.LiveMonsters.First().Key;
        Send(state, "Anomaly.Mob.Gone", new { uid, room = state.CurrentRoom, why = "left" });
        Assert.True(state.StaticMonsters.ContainsKey(uid));
        Assert.False(state.LiveMonsters.ContainsKey(uid));
        Send(state, "Anomaly.Mob.Gone", new { uid, room = state.CurrentRoom, why = "died" });
        Assert.False(state.StaticMonsters.ContainsKey(uid));
    }

    [Fact]
    public void PartialDelta_PreservesOtherLiveFieldsAndCanClearTarget()
    {
        GmcpState state = new(); Replay(state, "07-recovery-request");
        long uid = state.LiveMonsters.First().Key;
        int max = state.LiveMonsters[uid].GetProperty("maxhp").GetInt32();
        Send(state, "Anomaly.Mob.Update", new { uid, room = state.CurrentRoom, full = false, hp = 7, target = (string?)null });
        Assert.Equal(7, state.LiveMonsters[uid].GetProperty("hp").GetInt32());
        Assert.Equal(max, state.LiveMonsters[uid].GetProperty("maxhp").GetInt32());
        Assert.Equal(JsonValueKind.Null, state.LiveMonsters[uid].GetProperty("target").ValueKind);
    }

    [Fact]
    public void DeltaWithoutBaseline_IsNotUsedAsCompleteState()
    {
        GmcpState state = new(); Replay(state, "01-handshake");
        Send(state, "Anomaly.Mob.Update", new { uid = 999, room = state.CurrentRoom, full = false, hp = 7 });
        Assert.Empty(state.LiveMonsters);
    }

    [Fact]
    public void PackageRemoval_DoesNotDiscardUnrelatedSnapshots()
    {
        GmcpState state = new(); Replay(state, "07-recovery-request");
        state.SetMode("Anomaly.Char.Vitals", GmcpMode.Active);
        Send(state, "Anomaly.Session.Reset", new { scope = "package", package = "Anomaly.Mob" });
        Assert.Empty(state.StaticMonsters); Assert.Empty(state.LiveMonsters);
        Assert.True(state.Owns("Anomaly.Char.Vitals"));
        Assert.NotNull(state.Snapshot("Anomaly.Room.Info"));
        Assert.False(state.RosterComplete);
    }

    [Fact]
    public void UnknownMinorFields_AreIgnoredAndMajorMismatchFallsBack()
    {
        GmcpState state = new(); Replay(state, "01-handshake");
        Send(state, "Anomaly.Server", new { name = "Anomaly", version = "1", contract = "1.1.0", boot = "0000000000000001", build = "test", optionalFutureField = 42 });
        Assert.True(state.Compatible); Assert.Empty(state.StaticMonsters);
        Send(state, "Anomaly.Session.Reset", new { scope = "all", reason = "enter" });
        Send(state, "Anomaly.Server", new { name = "Anomaly", version = "1", contract = "2.0.0", boot = "0000000000000002", build = "test" });
        Assert.False(state.Compatible); Assert.False(state.InSession);
        Assert.Null(state.Snapshot("Anomaly.Char.Vitals"));
    }

    [Fact]
    public void InvalidSnapshot_CannotDisableTextParser()
    {
        GmcpState state = new(); Replay(state, "01-handshake");
        state.SetMode("Anomaly.Char.Vitals", GmcpMode.Active);
        Assert.True(state.Owns("Anomaly.Char.Vitals"));
        Send(state, "Anomaly.Char.Vitals", new { hp = 1 });
        Assert.False(state.Owns("Anomaly.Char.Vitals"));
        Assert.NotNull(state.Snapshot("Anomaly.Char.Status"));
    }

    [Fact]
    public void Disconnect_RevokesAllAuthorityAndRuntimeIdentities()
    {
        GmcpState state = new(); Replay(state, "07-recovery-request");
        state.SetMode("Anomaly.Char.Vitals", GmcpMode.Active);
        state.Disconnected();
        Assert.False(state.Owns("Anomaly.Char.Vitals"));
        Assert.Null(state.Boot); Assert.Null(state.CurrentRoom);
        Assert.Empty(state.StaticMonsters); Assert.Empty(state.LiveMonsters);
    }
}
