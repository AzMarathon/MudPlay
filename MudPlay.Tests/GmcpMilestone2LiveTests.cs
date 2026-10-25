using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Gmcp;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

public sealed partial class GmcpIntegrationTests
{
    private static async Task Command(Session session, string command)
    {
        session.ClearText();
        await session.Telnet.SendTextAsync(command + "\r\n");
        await Until(() => session.Transcript.Contains("[HP=", StringComparison.Ordinal), "command prompt for " + command,
            ended: () => session.Telnet.EndedBecause);
        await Task.Delay(200);
    }

    private static void AssertHealthy(Session session)
    {
        Assert.DoesNotContain(session.Log.Snapshot(), e => e.Source == "GMCP" &&
            (e.Message.Contains("Rejected", StringComparison.Ordinal) || e.Message.Contains("Cannot apply", StringComparison.Ordinal)));
    }

    [LiveGmcpFact]
    public async Task IsolatedHost_Milestone2Shadow()
    {
        using Session session = new(active: false);
        session.Party.LocalCharacterName = "Gthief";
        List<ChatLogEntry> chat = new(); session.Chat.EntryClassified += e => chat.Add(e);
        try
        {
            await Login(session, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_ACCOUNT")!, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_PASSWORD")!);
            foreach (string command in new[] { "i", "spells", "par", "abil 31", "gossip gmcp m2 shadow repeat", "gossip gmcp m2 shadow repeat" })
                await Command(session, command);
            await Until(() => session.Read(() => chat.Count(e => e.Message == "gmcp m2 shadow repeat") == 2), "two repeated text chat messages in shadow");
            Assert.All(new[] { "Anomaly.Items.List", "Anomaly.Spells.List", "Anomaly.Char.Effects", "Anomaly.Char.Abilities", "Anomaly.Group.Members", "Anomaly.Shop.List" }, package =>
            {
                Assert.NotNull(session.Read(() => session.Gmcp.State.Snapshot(package)));
                Assert.False(session.Read(() => session.Gmcp.State.Owns(package)));
            });
            Assert.Equal(JsonValueKind.Null, session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Shop.List")!.Value.GetProperty("shop").ValueKind));
            AssertHealthy(session);
            Assert.Empty(session.Read(() => session.Log.Snapshot().Where(e => e.Source == "GMCP shadow").Select(e => e.Message).ToArray()));
            await session.Telnet.SendTextAsync("quit\r\n");
        }
        finally { await session.Telnet.DisconnectAsync(); }
    }

    [LiveGmcpFact]
    public async Task IsolatedHost_Milestone2Active()
    {
        using Session session = new(); using Session priest = new(); using Session sysop = new(active: false);
        session.Party.LocalCharacterName = "Gthief"; priest.Party.LocalCharacterName = "Gpriest"; sysop.Party.LocalCharacterName = "Gsysop";
        List<ChatLogEntry> chat = new(); session.Chat.EntryClassified += chat.Add;
        bool party = false;
        try
        {
            await Login(session, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_ACCOUNT")!, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_PASSWORD")!);
            Assert.Equal("1,1", session.Read(() => session.Gmcp.State.CurrentRoom));
            await Until(() => session.Read(() => session.Inventory.IsLoaded), "server inventory owner");
            int num = session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Items.List")!.Value.GetProperty("inv").EnumerateArray().First().GetProperty("num").GetInt32());
            await session.Gmcp.RequestItemDetailAsync(num); await session.Gmcp.RequestItemUsabilityAsync(num);
            await Until(() => session.Read(() => session.Gmcp.State.ItemCards.ContainsKey(num) && session.Gmcp.State.ItemUsability.ContainsKey(num)), "requested static card and live usability");
            await session.Gmcp.RequestItemUsabilityAsync(int.MaxValue);
            await Until(() => session.Read(() => session.Gmcp.State.ItemUsability.TryGetValue(int.MaxValue, out var unknown) && unknown.GetProperty("unknown").GetBoolean()), "unknown item reply");
            long coins = session.Read(() => session.Inventory.Snapshot.Currency.TotalCopperValue);
            await Command(session, "drop 1 copper");
            await Until(() => session.Read(() => session.Inventory.Snapshot.Currency.TotalCopperValue == coins - 1), "one coin removed once");
            await Command(session, "get 1 copper");
            await Until(() => session.Read(() => session.Inventory.Snapshot.Currency.TotalCopperValue == coins), "one coin restored once");
            await Login(priest, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_PRIEST_ACCOUNT")!, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_PRIEST_PASSWORD")!);
            await Command(session, "invite Gpriest"); await Command(priest, "follow Gthief"); party = true;
            await Command(priest, "backrank");
            await Until(() => session.Read(() => session.Party.State.Members.Any(m => m.Name.Contains("Gpriest", StringComparison.OrdinalIgnoreCase) && !m.IsInvited && m.Rank == Models.Profile.PartyRank.Back)), "party membership and formation rank");
            await Command(priest, "say gmcp m2 active repeat"); await Command(priest, "say gmcp m2 active repeat");
            await Until(() => session.Read(() => chat.Count(e => e.Message == "gmcp m2 active repeat") == 2), "exactly two repeated GMCP chat events");
            await Command(priest, ">Gthief gmcp m2 directed");
            await Until(() => session.Read(() => chat.Count(e => e.Message == "gmcp m2 directed" && e.DirectedTo is not null) == 1), "directed say retained on its text source");
            await Login(sysop, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_SYSOP_ACCOUNT")!, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_SYSOP_PASSWORD")!);
            await Command(sysop, "sysop god Gpriest grantspell 14");
            await Until(() => priest.Read(() => priest.Book.Available.Any(s => s.Number == 14)), "learned bless by server id");
            await Command(priest, "bles Gthief");
            await Until(() => session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Char.Effects")!.Value.GetProperty("effects").EnumerateArray().Any(e => e.GetProperty("spell").GetInt32() == 14)), "cast effect snapshot");
            Assert.Contains(session.Read(() => session.Director.SnapshotActiveBuffs()), timer => timer.TotalSec > 0);
            await Command(priest, "leave"); party = false;
            await Until(() => session.Read(() => !session.Party.State.IsInParty), "party leave snapshot");
            foreach (string direction in Enumerable.Repeat("w", 9).Concat(new[] { "n", "w", "w", "w" })) await Move(session, direction);
            await Until(() => session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Shop.List") is { } shop && shop.GetProperty("shop").ValueKind == JsonValueKind.Number), "real room-bound shop stock");
            Assert.Null(session.Shop.DescribePending());
            List<IReadOnlyList<ShopListParser.StockRow>> lists = new(); session.Shop.StockListed += lists.Add;
            await Command(session, "list");
            await Until(() => session.Read(() => lists.Count > 0 && lists[^1].Count > 0), "explicit shop list uses GMCP stock");
            foreach (string direction in new[] { "e", "e", "e", "s" }.Concat(Enumerable.Repeat("e", 9))) await Move(session, direction);
            await Until(() => session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Shop.List") is { } shop && shop.GetProperty("shop").ValueKind == JsonValueKind.Null), "empty shop outside shop room");
            Assert.True(session.Read(() => session.Gmcp.State.ItemCards.ContainsKey(num)));
            AssertHealthy(session); AssertHealthy(priest);
        }
        finally
        {
            if (party) await priest.Telnet.SendTextAsync("leave\r\n");
            foreach (Session peer in new[] { session, priest, sysop })
            {
                await peer.Telnet.SendTextAsync("quit\r\n");
                await peer.Telnet.DisconnectAsync();
            }
        }
    }
}
