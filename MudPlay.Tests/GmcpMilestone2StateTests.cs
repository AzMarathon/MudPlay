using System.Text.Json;
using MudPlay.Game.Gmcp;
using MudPlay.Net;
using Xunit;

namespace MudPlay.Tests;

public sealed class GmcpMilestone2StateTests
{
    internal static JsonElement Example(string package) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Gmcp", "Examples", "valid", package + ".json"))).RootElement.Clone();
    internal static GmcpState Enter()
    {
        GmcpState state = new();
        state.Receive(new("Anomaly.Server", Example("Anomaly.Server")));
        GmcpStateTests.Send(state, "Anomaly.Session.Reset", new { scope = "all", reason = "enter" });
        GmcpStateTests.Send(state, "Anomaly.Session.Reset", new { scope = "room", room = "1,1" });
        return state;
    }

    [Theory]
    [InlineData("Anomaly.Items.List")]
    [InlineData("Anomaly.Items.Detail")]
    [InlineData("Anomaly.Items.Usable")]
    [InlineData("Anomaly.Shop.List")]
    [InlineData("Anomaly.Spells.List")]
    [InlineData("Anomaly.Char.Effects")]
    [InlineData("Anomaly.Char.Abilities")]
    [InlineData("Anomaly.Group.Members")]
    [InlineData("Anomaly.Comm.Line")]
    public void TaggedExamples_AreAccepted(string package)
    {
        Assert.True(GmcpContract.IsValid(package, Example(package)));
    }

    [Fact]
    public void FullMilestone2Recording_AcceptsLargeSnapshotsAndKeepsBothRepeatedEvents()
    {
        GmcpState state = new();
        List<string> errors = new(); List<JsonElement> lines = new();
        state.Diagnostic += errors.Add; state.Communication += lines.Add;
        GmcpStateTests.Replay(state, "13-milestone2");
        Assert.Empty(errors);
        Assert.Equal(60, state.Snapshot("Anomaly.Spells.List")!.Value.GetProperty("spells").GetArrayLength());
        Assert.Equal(2, lines.Count(l => l.GetProperty("text").GetString() == "inc"));
        Assert.Null(state.Snapshot("Anomaly.Comm.Line"));
        Assert.NotEmpty(state.ItemCards);
        Assert.Equal(JsonValueKind.Null, state.Snapshot("Anomaly.Shop.List")!.Value.GetProperty("shop").ValueKind);
    }

    [Fact]
    public void Cards_SurviveRoomChangesButNotReloadOrCharacterReset()
    {
        GmcpState state = Enter(); state.Receive(new("Anomaly.Items.Detail", Example("Anomaly.Items.Detail")));
        GmcpStateTests.Send(state, "Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        Assert.Single(state.ItemCards);
        GmcpStateTests.Send(state, "Anomaly.Session.Reset", new { scope = "package", package = "Anomaly.Items.Detail" });
        Assert.Empty(state.ItemCards);
        state.Receive(new("Anomaly.Items.Detail", Example("Anomaly.Items.Detail")));
        GmcpStateTests.Send(state, "Anomaly.Session.Reset", new { scope = "all", reason = "character" });
        Assert.Empty(state.ItemCards);
    }

    [Fact]
    public void ShopAndUsability_DoNotOutliveTheirScope()
    {
        GmcpState state = Enter();
        GmcpStateTests.Send(state, "Anomaly.Shop.List", new { room = "1,1", shop = (int?)null, name = (string?)null, items = Array.Empty<object>() });
        state.Receive(new("Anomaly.Items.Usable", Example("Anomaly.Items.Usable")));
        state.Receive(new("Anomaly.Char.Vitals", Example("Anomaly.Char.Vitals")));
        Assert.Empty(state.ItemUsability);
        GmcpStateTests.Send(state, "Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        Assert.Null(state.Snapshot("Anomaly.Shop.List"));
        GmcpStateTests.Send(state, "Anomaly.Shop.List", new { room = "1,1", shop = (int?)null, name = (string?)null, items = Array.Empty<object>() });
        Assert.Null(state.Snapshot("Anomaly.Shop.List"));
    }

    [Fact]
    public void Usability_SurvivesVitalsTicks_AndExpiresOnLevelOrAlignment()
    {
        GmcpState state = Enter();
        JsonElement Changed(string example, string field, System.Text.Json.Nodes.JsonNode value)
        {
            System.Text.Json.Nodes.JsonObject node = System.Text.Json.Nodes.JsonNode.Parse(Example(example).GetRawText())!.AsObject();
            node[field] = value;
            return JsonSerializer.SerializeToElement(node);
        }
        state.Receive(new("Anomaly.Char.Vitals", Example("Anomaly.Char.Vitals")));
        state.Receive(new("Anomaly.Char.Status", Example("Anomaly.Char.Status")));
        state.Receive(new("Anomaly.Items.Usable", Example("Anomaly.Items.Usable")));
        state.Receive(new("Anomaly.Char.Vitals", Changed("Anomaly.Char.Vitals", "hp", 1)));
        Assert.Single(state.ItemUsability);
        state.Receive(new("Anomaly.Char.Vitals", Changed("Anomaly.Char.Vitals", "level", 99)));
        Assert.Empty(state.ItemUsability);
        state.Receive(new("Anomaly.Items.Usable", Example("Anomaly.Items.Usable")));
        state.Receive(new("Anomaly.Char.Status", Changed("Anomaly.Char.Status", "evilPoints", 250)));
        Assert.Empty(state.ItemUsability);
    }

    [Fact]
    public void NegativePartyHealthAndZeroAbilities_ArePreserved()
    {
        GmcpState state = Enter();
        state.Receive(new("Anomaly.Group.Members", Example("Anomaly.Group.Members.2")));
        Assert.Contains(state.Snapshot("Anomaly.Group.Members")!.Value.GetProperty("members").EnumerateArray(), m => m.GetProperty("hpPct").GetInt32() < 0);
        state.Receive(new("Anomaly.Char.Abilities", Example("Anomaly.Char.Abilities")));
        JsonElement flag = state.Snapshot("Anomaly.Char.Abilities")!.Value.GetProperty("abilities").EnumerateArray().Single(a => a.GetProperty("id").GetInt32() == 186);
        Assert.Equal(0, flag.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, flag.GetProperty("sources").GetProperty("worn").ValueKind);
    }

    [Fact]
    public void RefreshOrModeChanges_DoNotReplayCommunication()
    {
        GmcpState state = Enter(); int events = 0; state.Communication += _ => events++;
        state.SetMode("Anomaly.Comm.Line", GmcpMode.Active);
        Assert.True(state.OwnsCommunication);
        state.Receive(new("Anomaly.Comm.Line", Example("Anomaly.Comm.Line")));
        state.SetMode("Anomaly.Comm.Line", GmcpMode.Shadow);
        state.SetMode("Anomaly.Comm.Line", GmcpMode.Active);
        Assert.Equal(1, events);
        state.Disconnected(); Assert.False(state.OwnsCommunication);
    }
    [Fact]
    public void InvalidShopScopeAndPartyPercent_RestoreOnlyAffectedTextAuthority()
    {
        GmcpState state = Enter();
        state.SetMode("Anomaly.Shop.List", GmcpMode.Active);
        state.SetMode("Anomaly.Group.Members", GmcpMode.Active);
        state.Receive(new("Anomaly.Group.Members", Example("Anomaly.Group.Members.2")));
        GmcpStateTests.Send(state, "Anomaly.Shop.List", new { room = "1,1", shop = (int?)null, name = "stale shop", items = Array.Empty<object>() });
        Assert.False(state.Owns("Anomaly.Shop.List"));
        Assert.True(state.Owns("Anomaly.Group.Members"));
        var group = Example("Anomaly.Group.Members.2");
        var members = group.GetProperty("members").EnumerateArray().Select(m => m.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)).ToArray();
        members[0]["hpPct"] = JsonSerializer.SerializeToElement(101);
        GmcpStateTests.Send(state, "Anomaly.Group.Members", new { leader = group.GetProperty("leader"), members });
        Assert.False(state.Owns("Anomaly.Group.Members"));
    }

}
