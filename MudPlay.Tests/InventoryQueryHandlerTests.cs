using System.Reflection;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Pins the read-only <see cref="InventoryQueryHandler"/> replies
/// (<c>@wealth</c> / <c>@enc</c> / <c>@have</c> / <c>@what</c>): the
/// wealth / carry / have trio read the immutable
/// <see cref="InventoryManager.Snapshot"/> and gate on "parse inventory
/// first" until the first full <c>i</c> dump lands; <c>@what</c> reads the
/// <see cref="GroundItemTracker"/>'s last "You notice" survey.
/// </summary>
public sealed class InventoryQueryHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc);

    private static (RemoteCommandManager engine, InventoryManager inv, LineExtractor lines,
        PlayerDatabase players, MessageRouter router, GroundItemTracker ground)
        Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        InventoryManager inv = new(log: null, itemWeightResolver: null, slotResolver: null);
        LineExtractor lines = new(new TerminalEmulator(80, 24));
        inv.AttachLineExtractor(lines);
        GroundItemTracker ground = new(router, new CurrencyNaming());
        _ = new InventoryQueryHandler(engine, inv, ground);
        return (engine, inv, lines, players, router, ground);
    }

    private static void Feed(LineExtractor lines, string text)
    {
        FieldInfo? field = typeof(LineExtractor).GetField(
            "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(lines) is Action<LineExtractor.EmittedLine> handler)
        {
            handler(new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }
    }

    // Push a single-line "You notice ... here." through the router so the
    // GroundItemTracker's pattern subscription fires (the same path a real
    // room survey travels).
    private static void FeedRoom(MessageRouter router, string text) =>
        router.Dispatch(new LineExtractor.EmittedLine(
            text, Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

    private static void FeedCurrencyDump(LineExtractor lines)
    {
        Feed(lines, "You are carrying 2 runic coins, 6 platinum pieces, 94 gold crowns, "
                  + "2 silver nobles, 5 copper farthings.");
        Feed(lines, "You have no keys.");
        Feed(lines, "Wealth:    2069425 copper farthings");
        Feed(lines, "Encumbrance:    36/2880  -  Light  [1%]");
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static List<string> Replies(RemoteCommandManager engine) =>
        engine.LastSentForTests
            .Select(b => Encoding.Latin1.GetString(b))
            .Select(StripWire)
            .ToList();

    private static string StripWire(string wire)
    {
        string s = wire.TrimEnd('\r');
        int open = s.IndexOf('{');
        int close = s.LastIndexOf('}');
        return open >= 0 && close > open ? s[(open + 1)..close] : s;
    }

    // ----- @wealth -----------------------------------------------------

    [Fact]
    public void Wealth_BeforeParse_PointsAtInventory()
    {
        var (engine, _, _, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);

        engine.DispatchForTests(Telepath("Bob", "@wealth"));

        Assert.Contains("parse inventory first", Assert.Single(Replies(engine)));
    }

    [Fact]
    public void Wealth_ReportsCoinsHighToLow_WithCopperTotal()
    {
        var (engine, _, lines, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        FeedCurrencyDump(lines);

        engine.DispatchForTests(Telepath("Bob", "@wealth"));

        string reply = Assert.Single(Replies(engine));
        Assert.Equal(
            "2 runic, 6 platinum, 94 gold, 2 silver, 5 copper (= 2,069,425 copper)",
            reply);
    }

    // ----- @enc --------------------------------------------------------

    [Fact]
    public void Enc_BeforeParse_PointsAtInventory()
    {
        var (engine, _, _, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);

        engine.DispatchForTests(Telepath("Bob", "@enc"));

        Assert.Contains("parse inventory first", Assert.Single(Replies(engine)));
    }

    [Fact]
    public void Enc_ReportsCurMaxPctBracket()
    {
        var (engine, _, lines, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        FeedCurrencyDump(lines);

        engine.DispatchForTests(Telepath("Bob", "@enc"));

        Assert.Equal("Encumbrance 36/2880 (1%) - Light", Assert.Single(Replies(engine)));
    }

    // ----- @have -------------------------------------------------------

    [Fact]
    public void Have_WithoutArgs_ShowsUsage()
    {
        var (engine, _, lines, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        FeedCurrencyDump(lines);

        engine.DispatchForTests(Telepath("Bob", "@have"));

        Assert.Equal("usage: @have <item name>", Assert.Single(Replies(engine)));
    }

    [Fact]
    public void Have_BeforeParse_PointsAtInventory()
    {
        var (engine, _, _, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);

        engine.DispatchForTests(Telepath("Bob", "@have dagger"));

        Assert.Contains("inventory not parsed yet", Assert.Single(Replies(engine)));
    }

    [Fact]
    public void Have_MatchesCarriedAndEquipped_CaseInsensitiveSubstring()
    {
        var (engine, _, lines, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        Feed(lines, "You are carrying a rusty dagger, a healing potion, "
                  + "padded vest (Torso), a jagged dagger.");
        Feed(lines, "Encumbrance:    36/2880  -  Light  [1%]");

        engine.DispatchForTests(Telepath("Bob", "@have DAGGER"));

        Assert.Equal("yes - 2x 'DAGGER'", Assert.Single(Replies(engine)));
    }

    [Fact]
    public void Have_StackedItem_ReportsTheStackQuantity_NotOne()
    {
        // Report: another user @have'd an item the sender held 25 of (a stack prints
        // as one "25 black diamonds" line) and got "1x". The reply must report the
        // real quantity, echoing the queried name: "yes - 25x 'black diamond'".
        var (engine, _, lines, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        Feed(lines, "You are carrying a rusty dagger, 25 black diamonds, a healing potion.");
        Feed(lines, "Encumbrance:    36/2880  -  Light  [1%]");

        engine.DispatchForTests(Telepath("Bob", "@have black diamond"));

        Assert.Equal("yes - 25x 'black diamond'", Assert.Single(Replies(engine)));
    }

    [Fact]
    public void Have_NoMatch_ReportsNo()
    {
        var (engine, _, lines, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        Feed(lines, "You are carrying a rusty dagger, a healing potion.");
        Feed(lines, "Encumbrance:    36/2880  -  Light  [1%]");

        engine.DispatchForTests(Telepath("Bob", "@have longsword"));

        Assert.Equal("no - nothing matching 'longsword'", Assert.Single(Replies(engine)));
    }

    // ----- @inv --------------------------------------------------------

    [Fact]
    public void Inv_BeforeParse_PointsAtInventory()
    {
        var (engine, _, _, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);

        engine.DispatchForTests(Telepath("Bob", "@inv"));

        Assert.Contains("inventory not parsed yet", Assert.Single(Replies(engine)));
    }

    [Fact]
    public void Inv_ReportsCarriedPack_ExcludingWornAndCurrency()
    {
        var (engine, _, lines, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        // Worn piece (Torso) and a readied light are excluded; coins never
        // land in CarriedItems. Only the two unworn pack items report.
        Feed(lines, "You are carrying a rusty dagger, a healing potion, "
                  + "padded vest (Torso), lantern (Readied/240), 5 copper farthings.");
        Feed(lines, "You have no keys.");
        Feed(lines, "Encumbrance:    36/2880  -  Light  [1%]");

        engine.DispatchForTests(Telepath("Bob", "@inv"));

        Assert.Equal("carrying: a rusty dagger, a healing potion", Assert.Single(Replies(engine)));
    }

    [Fact]
    public void Inv_AppendsKeyRing_WhenKeysHeld()
    {
        var (engine, _, lines, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        Feed(lines, "You are carrying a brass lantern.");
        Feed(lines, "You have the following keys:  black star key, brass key.");
        Feed(lines, "Encumbrance:    36/2880  -  Light  [1%]");

        engine.DispatchForTests(Telepath("Bob", "@inv"));

        Assert.Equal("carrying: a brass lantern; keys: black star key, brass key",
            Assert.Single(Replies(engine)));
    }

    [Fact]
    public void Inv_EmptyPackNoKeys_ReportsCarryingNothing()
    {
        var (engine, _, lines, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        Feed(lines, "You are carrying nothing.");
        Feed(lines, "You have no keys.");
        Feed(lines, "Encumbrance:    0/2880  -  Light  [0%]");

        engine.DispatchForTests(Telepath("Bob", "@inv"));

        Assert.Equal("carrying nothing", Assert.Single(Replies(engine)));
    }

    [Fact]
    public void Inv_LongPack_SplitsAcrossReplies_ReportsEveryItem_NoTruncation()
    {
        var (engine, _, lines, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        // 40 padded item names blow past a single say's budget; the WHOLE pack must
        // go out across multiple chunked replies rather than a "+N more" summary.
        var items = Enumerable.Range(1, 40).Select(i => $"a padded widget number {i}").ToList();
        Feed(lines, $"You are carrying {string.Join(", ", items)}.");
        Feed(lines, "Encumbrance:    36/2880  -  Light  [1%]");

        engine.DispatchForTests(Telepath("Bob", "@inv"));

        List<string> replies = Replies(engine);
        Assert.True(replies.Count > 1, "a long pack should split across multiple replies");
        // No truncation summary anywhere.
        Assert.DoesNotContain(replies, r => r.Contains("more, type i)"));
        // Every item appears across the replies, and each reply fits a say line.
        string all = string.Join(" ", replies);
        foreach (string item in items)
            Assert.Contains(item, all);
        foreach (string r in replies)
            Assert.True(r.Length < 200, $"reply too long ({r.Length}): {r}");
        // Split parts are labelled "carrying (i/N): …".
        Assert.All(replies, r => Assert.StartsWith("carrying (", r));
    }

    // ----- @what -------------------------------------------------------

    [Fact]
    public void What_NoSurvey_ReportsNothing()
    {
        var (engine, _, _, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);

        engine.DispatchForTests(Telepath("Bob", "@what"));

        Assert.Equal("nothing on the ground here", Assert.Single(Replies(engine)));
    }

    [Fact]
    public void What_ReportsGroundItems_ExcludingCash()
    {
        var (engine, _, _, players, router, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        // Survey mixes loot and coin — coin is filtered, items keep wording.
        FeedRoom(router, "You notice a rusty dagger, 5 gold crowns and a healing potion here.");

        engine.DispatchForTests(Telepath("Bob", "@what"));

        Assert.Equal("on the ground: a rusty dagger, a healing potion",
            Assert.Single(Replies(engine)));
    }

    // ----- gating ------------------------------------------------------

    [Fact]
    public void Wealth_FromUnauthorisedSender_IsDenied()
    {
        var (engine, _, lines, players, _, _) = Setup();
        engine.WarnOnDenial = false;
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.QueryInventory);
        FeedCurrencyDump(lines);

        engine.DispatchForTests(Telepath("Stranger", "@wealth"));

        Assert.Empty(Replies(engine));
    }
}
