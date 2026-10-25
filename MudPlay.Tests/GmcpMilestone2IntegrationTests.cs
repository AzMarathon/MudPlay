using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Gmcp;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed partial class GmcpIntegrationTests
{
    private static void EnterMilestone2(Session session)
    {
        session.Gmcp.Receive(new("Anomaly.Server", GmcpMilestone2StateTests.Example("Anomaly.Server")));
        session.Send("Anomaly.Session.Reset", new { scope = "all", reason = "enter" });
        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,1" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Milestone2Replay_DrivesRealOwnersAndDoesNotReplayChat(bool captured)
    {
        using Session session = new(); List<ChatLogEntry> chat = new(); session.Chat.EntryClassified += chat.Add;
        bool inventorySeen = false, cardSeen = false;
        session.Inventory.FullInventoryParsed += () => inventorySeen |= session.Inventory.IsLoaded;
        session.Gmcp.State.Changed += _ => cardSeen |= session.Gmcp.State.ItemCards.Count > 0;
        if (captured) session.ReplayCapture("13-milestone2"); else session.Replay("13-milestone2");
        Assert.True(inventorySeen);
        Assert.True(cardSeen);
        Assert.DoesNotContain(session.Log.Snapshot(), e => e.Source == "GMCP" && (e.Message.Contains("Rejected", StringComparison.Ordinal) || e.Message.Contains("Cannot apply", StringComparison.Ordinal)));
        Assert.Equal(captured ? 2 : 4, chat.Count(e => e.Message == (captured ? "testing the chat feed" : "inc")) + (captured ? 0 : chat.Count(e => e.Message == "follow me" || e.Message == "meet at the gates")));
        int count = chat.Count;
        session.Gmcp.State.SetMode("Anomaly.Items.Detail", GmcpMode.Active);
        Assert.Equal(count, chat.Count);
    }

    [Fact]
    public void ActiveInventory_IgnoresDuplicateTextMutationsButKeepsTransactionEvents()
    {
        using Session s = new(); EnterMilestone2(s);
        s.Gmcp.Receive(new("Anomaly.Items.List", GmcpMilestone2StateTests.Example("Anomaly.Items.List")));
        long wealth = s.Inventory.Snapshot.Currency.TotalCopperValue;
        int deposits = 0; s.Inventory.BankDeposited += _ => deposits++;
        s.Text("You deposit 1 gold crown.\r\nYou took a torch.\r\n");
        Assert.Equal(wealth, s.Inventory.Snapshot.Currency.TotalCopperValue);
        Assert.Equal(1, s.Inventory.Snapshot.PackCount("torch"));
        Assert.Equal(1, deposits);
        s.Send("Anomaly.Session.Reset", new { scope = "package", package = "Anomaly.Items" });
        Assert.False(s.Inventory.IsLoaded);
        s.Text("You are carrying 2 gold crowns, a torch\r\nYou have no keys.\r\nWealth: 200 copper farthings\r\nEncumbrance: 1/100 - None [1%]\r\n");
        Assert.Equal(200, s.Inventory.Snapshot.Currency.TotalCopperValue);
    }

    [Fact]
    public void PartySnapshot_PreservesNegativeHealthInvitationAndRankWithoutTextOverwrites()
    {
        using Session s = new(); EnterMilestone2(s);
        s.Gmcp.Receive(new("Anomaly.Group.Members", GmcpMilestone2StateTests.Example("Anomaly.Group.Members.2")));
        PartyMember down = s.Party.State.Members.Single(m => m.HpPercent < 0);
        int hp = down.HpPercent;
        s.Text("The following people are in your travel party:\r\n  Gpriest (Priest) [M: 80%] [H:100%] - Frontrank\r\n");
        Assert.Equal(hp, down.HpPercent);
        Assert.True(s.Party.State.IsInParty);
    }

    [Fact]
    public void Spellbook_UsesIdsAndDisplaysServerBandsForBothSharedCastCodes()
    {
        using Session s = new(); EnterMilestone2(s);
        s.Gmcp.Receive(new("Anomaly.Spells.List", GmcpMilestone2StateTests.Example("Anomaly.Spells.List")));
        Assert.Equal(2, s.Book.ObtainedCount);
        Assert.NotEqual(s.Book.Available[0].Number, s.Book.Available[1].Number);
        Assert.All(s.Book.Available, spell => Assert.Equal("blaz", spell.Short));
        s.Text("You have no spells.\r\n");
        Assert.Equal(2, s.Book.ObtainedCount);
        Assert.NotNull(s.Book.ServerSpell(s.Book.Available[1].Number));
    }

    [Fact]
    public void AbilityOwner_EmitsEngineTotalAndRetainsPresentZeroSources()
    {
        using Session s = new(); EnterMilestone2(s); List<AbilBreakdown> events = new(); s.Abilities.BreakdownParsed += events.Add;
        s.Gmcp.Receive(new("Anomaly.Char.Abilities", GmcpMilestone2StateTests.Example("Anomaly.Char.Abilities")));
        Assert.Equal(0, events.Single(e => e.Code == 186).Total);
        Assert.Contains(events.Single(e => e.Code == 186).Contributions, source => source.Source == "granted" && source.Value == 0);
        Assert.Equal(3, events.Single(e => e.Code == 101).Total);
        Assert.Equal(5, events.Single(e => e.Code == 101).From("worn"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChatAuthority_FiresHistoryAndTriggerOnceForEachIdenticalEvent(bool packetFirst)
    {
        using Session s = new(); EnterMilestone2(s);
        ProfileService profiles = TestProfiles.Loaded(); TriggerEngine triggers = new(profiles, s.Chat, s.Log, onUiThread: action => action());
        triggers.AttachLineExtractor(s.Feed.Lines); int fires = 0;
        triggers.SetSender(_ => fires++);
        triggers.Add(new Trigger("chat probe", true, TriggerScope.ChatAny, TriggerMatchType.Literal, "inc", "say got it"));
        triggers.Add(new Trigger("no parallel game-message delivery", true, TriggerScope.GameMessages, TriggerMatchType.Literal, "inc", "say duplicate"));
        List<ChatLogEntry> history = new(); s.Chat.EntryClassified += history.Add;
        for (int i = 0; i < 2; i++)
        {
            if (packetFirst) s.Send("Anomaly.Comm.Line", new { channel = "gossip", from = "Gpriest", to = (string?)null, text = "inc" });
            s.Text("Gpriest gossips: inc\r\n");
            if (!packetFirst) s.Send("Anomaly.Comm.Line", new { channel = "gossip", from = "Gpriest", to = (string?)null, text = "inc" });
        }
        Assert.Equal(2, history.Count(e => e.Message == "inc"));
        Assert.True(fires == 2, $"Expected 2 sends, got {fires}; triggers={triggers.Triggers.Count}: " + string.Join(" | ", s.Log.Snapshot().Select(e => e.Message)));
        s.Gmcp.Disconnected(); s.Text("Gpriest gossips: inc\r\n");
        Assert.Equal(3, history.Count(e => e.Message == "inc"));
        Assert.Equal(3, fires);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CommunicationReset_StaysOnTextUntilANewSessionWithoutDuplicatingFirstEvent(bool packetFirst)
    {
        using Session s = new(); EnterMilestone2(s);
        List<ChatLogEntry> history = new(); s.Chat.EntryClassified += history.Add;
        s.Send("Anomaly.Session.Reset", new { scope = "package", package = "Anomaly.Comm" });
        for (int i = 0; i < 2; i++)
        {
            if (packetFirst) s.Send("Anomaly.Comm.Line", new { channel = "gossip", from = "Gpriest", to = (string?)null, text = "inc" });
            s.Text("Gpriest gossips: inc\r\n");
            if (!packetFirst) s.Send("Anomaly.Comm.Line", new { channel = "gossip", from = "Gpriest", to = (string?)null, text = "inc" });
        }
        Assert.False(s.Gmcp.State.OwnsCommunication);
        Assert.Equal(2, history.Count(e => e.Message == "inc"));
        s.Send("Anomaly.Session.Reset", new { scope = "all", reason = "enter" });
        Assert.True(s.Gmcp.State.OwnsCommunication);
        s.Send("Anomaly.Comm.Line", new { channel = "gossip", from = "Gpriest", to = (string?)null, text = "inc" });
        s.Text("Gpriest gossips: inc\r\n");
        Assert.Equal(3, history.Count(e => e.Message == "inc"));
    }

    [Fact]
    public void ServerEffects_UseThreeSecondRoundsAndShowUnknownSpellWithoutInventingCasterOrLevel()
    {
        using Session s = new(); EnterMilestone2(s);
        s.Gmcp.Receive(new("Anomaly.Spells.List", GmcpMilestone2StateTests.Example("Anomaly.Spells.List")));
        var spell = s.Book.Available[0];
        DateTime before = DateTime.UtcNow;
        s.Send("Anomaly.Char.Effects", new { effects = new[] {
            new { spell = spell.Number, name = spell.Name, roundsLeft = (int?)10, magnitude = 37, caster = (string?)null },
            new { spell = 999999, name = "Unknown blessing", roundsLeft = (int?)20, magnitude = 0, caster = (string?)null }
        }});
        Assert.True(s.Director.IsSelfBuffUp(spell.Short));
        Assert.Equal(2, s.Director.SnapshotActiveBuffs().Count);
        var timer = s.Director.SnapshotActiveBuffs().Single(t => t.Short == spell.Short);
        Assert.Equal(30, timer.TotalSec);
        Assert.InRange(timer.Until, before.AddSeconds(30), DateTime.UtcNow.AddSeconds(30));
        Assert.Contains(s.Director.SnapshotActiveBuffs(), t => t.Short == "Unknown blessing" && t.TotalSec == 60);
        s.Send("Anomaly.Char.Effects", new { effects = new[] {
            new { spell = spell.Number, name = spell.Name, roundsLeft = (int?)null, magnitude = 37, caster = (string?)null }
        }});
        Assert.True(s.Director.IsSelfBuffUp(spell.Short));
        Assert.Empty(s.Director.SnapshotActiveBuffs());
        s.Send("Anomaly.Char.Effects", new { effects = Array.Empty<object>() });
        Assert.False(s.Director.IsSelfBuffUp(spell.Short));
    }

    [Fact]
    public void ShopArrival_DoesNotBuyAndExplicitListWaitsForFreshServerStock()
    {
        using Session s = new(); EnterMilestone2(s);
        List<string> requests = new(); List<IReadOnlyList<MudPlay.Game.Inventory.ShopListParser.StockRow>> lists = new();
        s.Gmcp.SetSender((package, payload) => { requests.Add(JsonSerializer.Serialize(payload)); return Task.CompletedTask; });
        s.Shop.StockListed += lists.Add;
        s.Send("Anomaly.Shop.List", new { room = "1,1", shop = 6, name = "Test shop", items = new[] {
            new { num = 1, name = "torch", price = new { amount = 10, currency = "copper" }, stock = 2, max = 5, restockMinutes = (int?)null, canUse = true, cannotUseBecause = Array.Empty<string>() }
        }});
        Assert.Empty(lists); Assert.Null(s.Shop.DescribePending());
        s.Text("The following items are for sale here:\r\n  stale text wares\r\n\r\n");
        Assert.Contains(requests, r => r.Contains("Anomaly.Shop.List", StringComparison.Ordinal)); Assert.Empty(lists);
        s.Send("Anomaly.Shop.List", new { room = "1,1", shop = 6, name = "Test shop", items = new[] {
            new { num = 1, name = "torch", price = new { amount = 12, currency = "copper" }, stock = 3, max = 5, restockMinutes = (int?)null, canUse = false, cannotUseBecause = new[] { "level" } }
        }});
        var row = Assert.Single(Assert.Single(lists));
        Assert.Equal(3, row.Quantity); Assert.True(row.TooPowerful);
        Assert.Equal("12 copper farthings", row.Price);
        s.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        Assert.Null(s.Shop.DescribePending());
    }

    [Fact]
    public void DirectedSay_RetainsTextHistoryWhenContractHasNoMatchingEvent()
    {
        using Session s = new(); EnterMilestone2(s);
        List<ChatLogEntry> history = new(); s.Chat.EntryClassified += history.Add;
        s.Text("Gpriest says (to you) \"healer ready\"\r\n");
        ChatLogEntry entry = Assert.Single(history);
        Assert.Equal("healer ready", entry.Message);
        Assert.NotNull(entry.DirectedTo);
    }

    [Fact]
    public async Task Inspector_RequestsTheSelectedItemAndDropsResetCardsWithoutReplayingEvents()
    {
        using Session s = new(); EnterMilestone2(s);
        using MudPlay.ViewModels.GmcpDataViewModel view = new(s.Gmcp);
        List<JsonElement> requests = new();
        s.Gmcp.SetSender((package, payload) => { requests.Add(JsonSerializer.SerializeToElement(payload)); return Task.CompletedTask; });
        JsonElement card = GmcpMilestone2StateTests.Example("Anomaly.Items.Detail");
        int num = card.GetProperty("num").GetInt32(); view.ItemNumber = num.ToString();
        await view.ItemCardCommand.ExecuteAsync(null);
        Assert.Contains(requests, r => r.GetProperty("package").GetString() == "Anomaly.Items.Detail" && r.GetProperty("num").GetInt32() == num);
        s.Gmcp.Receive(new("Anomaly.Items.Detail", card));
        Assert.Contains(card.GetProperty("name").GetString()!, view.Body);
        s.Send("Anomaly.Session.Reset", new { scope = "package", package = "Anomaly.Items.Detail" });
        Assert.StartsWith("No current data.", view.Body);
        await view.ItemUsabilityCommand.ExecuteAsync(null);
        Assert.Contains(requests, r => r.GetProperty("package").GetString() == "Anomaly.Items.Usable" && r.GetProperty("num").GetInt32() == num);
        s.Gmcp.SetSender((package, payload) => Task.FromException(new IOException("disconnected")));
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.StartsWith("Refresh failed:", view.Status);
    }

    [Fact]
    public void FailedShopRequest_DoesNotLeaveTheNextExplicitReadStuck()
    {
        using Session s = new(); EnterMilestone2(s);
        object shop = new { room = "1,1", shop = 6, name = "Test shop", items = Array.Empty<object>() };
        s.Send("Anomaly.Shop.List", shop);
        s.Gmcp.SetSender((package, payload) => Task.FromException(new IOException("disconnected")));
        s.Text("The following items are for sale here:\r\n");
        List<string> requests = new();
        s.Gmcp.SetSender((package, payload) => { requests.Add(JsonSerializer.Serialize(payload)); return Task.CompletedTask; });
        s.Text("The following items are for sale here:\r\n");
        Assert.Contains(requests, r => r.Contains("Anomaly.Shop.List", StringComparison.Ordinal));
        int reads = 0; s.Shop.StockListed += _ => reads++;
        s.Send("Anomaly.Shop.List", shop);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void SelfChat_PreservesNamedChannelRawTextForExistingTriggerPatterns()
    {
        using Session s = new(); EnterMilestone2(s); s.Party.LocalCharacterName = "Gthief";
        List<ChatLogEntry> history = new(); s.Chat.EntryClassified += history.Add;
        s.Send("Anomaly.Comm.Line", new { channel = "gossip", from = "Gthief", to = (string?)null, text = "inc" });
        s.Text("Gthief gossips: inc\r\n");
        Assert.Equal("Gthief gossips: inc", Assert.Single(history).RawText);
        s.Send("Anomaly.Comm.Line", new { channel = "say", from = "Gthief", to = (string?)null, text = "ready" });
        Assert.Equal("You say \"ready\"", history[^1].RawText);
    }

    [Fact]
    public void CapturedClientSession_ReplaysActualShopPartyBuffAndDirectedSayWithoutDuplicateChat()
    {
        using Session s = new(); s.Party.LocalCharacterName = "Gthief";
        bool shopSeen = false, effectSeen = false, memberSeen = false;
        s.Gmcp.State.Changed += _ =>
        {
            shopSeen |= s.Gmcp.State.Snapshot("Anomaly.Shop.List") is { } shop && shop.GetProperty("shop").ValueKind == JsonValueKind.Number && shop.GetProperty("items").GetArrayLength() > 0;
            effectSeen |= s.Gmcp.State.Snapshot("Anomaly.Char.Effects") is { } effects && effects.GetProperty("effects").EnumerateArray().Any(e => e.GetProperty("spell").GetInt32() == 14);
            memberSeen |= s.Party.State.Members.Any(m => m.Name == "Gpriest" && !m.IsInvited && m.Rank == Models.Profile.PartyRank.Back);
        };
        List<ChatLogEntry> history = new(); s.Chat.EntryClassified += history.Add;
        s.ReplayCapture("14-milestone2-client-active");
        Assert.True(shopSeen); Assert.True(effectSeen); Assert.True(memberSeen);
        Assert.Equal(2, history.Count(e => e.Message == "gmcp m2 active repeat"));
        Assert.Single(history.Where(e => e.Message == "gmcp m2 directed" && e.DirectedTo is not null));
        Assert.DoesNotContain(s.Log.Snapshot(), e => e.Source == "GMCP" && (e.Message.Contains("Rejected", StringComparison.Ordinal) || e.Message.Contains("Cannot apply", StringComparison.Ordinal)));
    }

    [Fact]
    public void InventorySupplementalLightRead_DoesNotCountTheLitCopyInThePackAgain()
    {
        using Session s = new(); EnterMilestone2(s);
        s.Gmcp.Receive(new("Anomaly.Items.List", GmcpMilestone2StateTests.Example("Anomaly.Items.List")));
        s.Text("You are carrying 2 gold crowns, torch (Readied/3).\r\nYou have no keys.\r\nWealth: 200 copper farthings\r\nEncumbrance: 1/100 - None [1%]\r\n");
        Assert.Equal("torch", s.Inventory.Snapshot.ReadiedLight?.Name);
        Assert.Equal(0, s.Inventory.Snapshot.PackCount("torch"));
    }

    [Fact]
    public void ItemNumbersBeyondEngineRange_DoNotCrashTheReceivePath()
    {
        using Session s = new(); EnterMilestone2(s);
        s.Send("Anomaly.Items.Detail", new { num = long.MaxValue, unknown = true });
        s.Send("Anomaly.Items.Usable", new { num = long.MaxValue, unknown = true });
        Assert.Empty(s.Gmcp.State.ItemCards); Assert.Empty(s.Gmcp.State.ItemUsability);
        s.Gmcp.Receive(new("Anomaly.Items.List", GmcpMilestone2StateTests.Example("Anomaly.Items.List")));
        var itemList = s.Gmcp.State.Snapshot("Anomaly.Items.List")!.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        var inv = itemList["inv"].EnumerateArray().Select(row => row.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)).ToArray();
        inv[0]["num"] = JsonSerializer.SerializeToElement(long.MaxValue); itemList["inv"] = JsonSerializer.SerializeToElement(inv);
        s.Send("Anomaly.Items.List", itemList);
        Assert.False(s.Gmcp.State.Owns("Anomaly.Items.List"));
    }

    [Fact]
    public void UnmappedItemBuff_KeepsExistingTrackingInsteadOfBeingDeclaredAbsent()
    {
        using Session s = new(); EnterMilestone2(s);
        s.Director.SetBuffDurationSources(_ => ("", 60L), _ => null);
        s.Director.SetItemCastSource(_ => 60L, _ => true);
        s.Director.NoteManualBuffCast("#test relic");
        s.Send("Anomaly.Char.Effects", new { effects = Array.Empty<object>() });
        Assert.True(s.Director.IsSelfBuffUp("#test relic"));
        Assert.Contains(s.Director.SnapshotActiveBuffs(), timer => timer.Short == "#test relic" && timer.TotalSec == 60);
    }

}
