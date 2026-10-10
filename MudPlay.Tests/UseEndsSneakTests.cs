using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Stealth;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// An item command ends a sneak and a hide when the item's spell is cast, and only
// then (GAME_MECHANICS "What ends a sneak", the item commands). The game prints
// nothing when it does, so the client has to know from the command it sent.
public sealed class UseEndsSneakTests : IDisposable
{
    private readonly string _root;

    public UseEndsSneakTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-use-sneak-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ----- the rule ------------------------------------------------------

    // Item rows shaped like the game data's: kind, then ability slots in order.
    // Ability 43 casts a spell on use, 114 marks the cast after it as a swing proc,
    // 42 teaches a spell, 54 is a light's illumination, 18 heals outright.
    private static readonly Dictionary<string, string> Items = new(StringComparer.OrdinalIgnoreCase)
    {
        ["waterskin"]            = Row(5, (43, 711)),
        ["iron ration"]          = Row(4, (43, 316)),
        ["torch"]                = Row(6, (54, 100)),
        ["incense"]              = Row(6, (43, 118)),
        ["brass key"]            = Row(7),
        ["scroll of shockshield"] = Row(9, (28, 1), (42, 64)),
        ["scroll of warding"]    = Row(9, (43, 500)),
        ["serpent wand"]         = Row(1, (43, 800)),
        ["flame blade"]          = Row(1, (114, 25), (43, 801)),
        ["nexus spear"]          = Row(1, (43, 72), (114, 25), (43, 431)),
        ["salve"]                = Row(0, (18, 20)),
        ["odd charm"]            = Row(0, (43, 999)),
        ["glowing token"]        = Row(10, (43, 72)),
    };

    // Spell Targets: 1 is cast with no target; 8 needs one named; 999 isn't on record.
    private static int? TargetsOf(int spell) => spell switch
    {
        711 or 316 or 118 or 500 or 72 => 1,
        800 or 801 or 431 => 8,
        _ => null,
    };

    private static string Row(int itemType, params (int Abil, int Val)[] slots)
    {
        StringBuilder sb = new($"{{\"ItemType\":{itemType}");
        for (int i = 0; i < slots.Length; i++)
            sb.Append($",\"Abil-{i}\":{slots[i].Abil},\"AbilVal-{i}\":{slots[i].Val}");
        return sb.Append('}').ToString();
    }

    private static ItemUseStealthRule Rule(params string[] held) => new(
        () => held,
        name => Items.TryGetValue(name, out string? json)
            ? ItemUseStealthRule.Facts.Read(JsonDocument.Parse(json).RootElement, TargetsOf)
            : null);

    private static readonly string[] Pack =
    {
        "waterskin", "2 iron ration", "torch", "incense", "brass key", "scroll of shockshield",
        "scroll of warding", "serpent wand", "flame blade", "nexus spear", "salve", "odd charm",
        "mystery box", "glowing token",
    };

    [Theory]
    // A cast with no target: the sneak ends.
    [InlineData("use waterskin", true)]
    [InlineData("us waterskin", true)]
    [InlineData("USE Waterskin", true)]
    [InlineData("drink waterskin", true)]
    [InlineData("dri waterskin", true)]
    [InlineData("eat iron ration", true)]
    [InlineData("use incense", true)]
    [InlineData("light incense", true)]
    [InlineData("lig incense", true)]
    [InlineData("read scroll of warding", true)]
    [InlineData("use nexus spear", true)]            // its on-use cast comes before the proc
    // Nothing is cast: the sneak holds.
    [InlineData("use torch", false)]
    [InlineData("light torch", false)]
    [InlineData("use brass key n", false)]
    [InlineData("use brass key", false)]
    [InlineData("read scroll of shockshield", false)]
    [InlineData("use salve", false)]
    [InlineData("use flame blade", false)]           // a swing proc isn't cast by a use
    // Refused before anything is cleared.
    [InlineData("use serpent wand", false)]          // `You must specify a target for that spell!`
    [InlineData("eat waterskin", false)]             // `eat` finds only food
    [InlineData("drink iron ration", false)]
    [InlineData("light waterskin", false)]
    [InlineData("use", false)]                       // the syntax line
    [InlineData("light", false)]                     // the light level
    [InlineData("read plaque", false)]               // not carried: a look
    // A word after the item may name a player or a monster: counted as ended.
    [InlineData("use serpent wand orc", true)]
    [InlineData("use waterskin bob", true)]
    // The client can't tell: counted as ended.
    [InlineData("use lever", true)]                  // not in the pack
    [InlineData("drink ale", true)]
    [InlineData("use mystery box", true)]            // carried, not in the game data
    [InlineData("use odd charm", true)]              // its spell isn't in the game data
    public void ItemCommand_EndsTheSneakOnlyWhenItsSpellIsCast(string command, bool ends) =>
        Assert.Equal(ends, SneakBreakingCommands.EndsSneak(command, items: Rule(Pack)));

    // Spellings the game doesn't take as these commands aren't item commands.
    [Theory]
    [InlineData("u waterskin")]
    [InlineData("rea scroll of warding")]            // `rea` is ready
    [InlineData("dr waterskin")]
    [InlineData("li incense")]
    [InlineData("ea iron ration")]
    public void ItemCommand_OnlyTheSpellingsTheGameTakes(string command) =>
        Assert.Null(ItemUseStealthRule.VerbOf(command.Split(' ')[0]));

    [Fact]
    public void ItemCommand_WithThePackUnread_CountsAsEnded_ExceptARead()
    {
        ItemUseStealthRule nothingHeld = Rule();
        Assert.True(SneakBreakingCommands.EndsSneak("use waterskin", items: nothingHeld));
        Assert.True(SneakBreakingCommands.EndsSneak("eat iron ration", items: nothingHeld));
        Assert.False(SneakBreakingCommands.EndsSneak("read scroll of warding", items: nothingHeld));
    }

    // ----- hide ----------------------------------------------------------

    [Theory]
    [InlineData("use waterskin", true)]
    [InlineData("sea", true)]
    [InlineData("wear plate mail", true)]
    [InlineData("rem torch", true)]
    [InlineData("open chest", true)]
    [InlineData("buy torch", true)]
    [InlineData("rest", true)]
    [InlineData(".hello", true)]
    [InlineData("disarm trap n", false)]
    [InlineData("follow bob", false)]
    [InlineData("move rock", false)]
    public void AlsoEndsHide_AllButTheCommandsWithNoHiddenClear(string command, bool endsHide) =>
        Assert.Equal(endsHide, SneakBreakingCommands.AlsoEndsHide(command));

    private sealed class World : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PlayerState State { get; } = new();
        public StealthManager Stealth { get; }
        public List<string> Wire { get; } = new();
        public bool EngineDriving;
        private readonly ItemUseStealthRule _items = Rule(Pack);

        public World()
        {
            DefaultPatterns.Seed(Router);
            Stealth = new StealthManager(Router, State, new LogService());
            Stealth.SetAutoToggles(() => true, () => false);
            Stealth.SetEngineDrivingCheck(() => EngineDriving);
            Stealth.SetWireSender(SendBytes);
        }

        // Every command goes out through here, as it does through SendUserInput, and
        // is judged the way AppServices.NoteSentForSneak judges it.
        public void Send(string command)
        {
            Wire.Add(command);
            if (SneakBreakingCommands.EndsSneak(command, items: _items))
                Stealth.NoteSneakBroken($"'{command}'", endsHide: SneakBreakingCommands.AlsoEndsHide(command));
        }

        public void SendBytes(byte[] bytes) => Send(Encoding.Latin1.GetString(bytes).TrimEnd('\r'));

        public void Feed(string line) =>
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Dispose() => Stealth.Dispose();
    }

    [Fact]
    public void Hidden_AUseThatCasts_DropsTheHide()
    {
        using World w = new();
        w.Feed("Attempting to hide...");
        Assert.True(w.State.IsHidden);

        w.Send("use waterskin");

        Assert.False(w.State.IsHidden);
        Assert.Equal(StealthState.Idle, w.Stealth.State);
        Assert.False(w.Stealth.IsStealthedHere);
    }

    [Fact]
    public void Hidden_ACommandThatCastsNothing_OrHasNoHiddenClear_KeepsTheHide()
    {
        using World w = new();
        w.Feed("Attempting to hide...");

        w.Send("use torch");
        w.Send("disarm trap n");
        w.Send("get sword");

        Assert.True(w.State.IsHidden);
        Assert.Equal(StealthState.Hidden, w.Stealth.State);
    }

    [Fact]
    public void Hidden_ASearchOrAGearSwap_DropsTheHide()
    {
        using World w = new();
        w.Feed("Attempting to hide...");
        w.Send("sea");
        Assert.False(w.State.IsHidden);

        w.Feed("Attempting to hide...");
        w.Send("wear plate mail");
        Assert.False(w.State.IsHidden);
    }

    // The game drops a hide as it takes the move command, so a move that goes nowhere
    // ends it without any room change to say so.
    [Fact]
    public void Hidden_AMoveSent_DropsTheHide_BeforeAnyRoomChange()
    {
        using World w = new() { EngineDriving = true };
        w.Feed("Attempting to hide...");

        w.Stealth.NoteTypedMove();

        Assert.False(w.State.IsHidden);
        Assert.Equal(StealthState.Idle, w.Stealth.State);
    }

    // ----- sneak ---------------------------------------------------------

    [Fact]
    public void Sneaking_AUseThatCasts_EndsTheSneak_AndTheNextMoveReSneaks()
    {
        using World w = new() { EngineDriving = true };
        w.Feed("Sneaking...");

        w.Send("use waterskin");
        Assert.False(w.State.IsSneaking);
        Assert.False(w.Stealth.IsStealthedHere);

        w.Stealth.RequestPreMoveStealth();
        Assert.Equal(new[] { "use waterskin", "sn" }, w.Wire);
    }

    [Fact]
    public void Sneaking_LightingATorch_KeepsTheSneak_AndSendsNoSn()
    {
        using World w = new() { EngineDriving = true };
        w.Feed("Sneaking...");

        w.Send("use torch");
        w.Stealth.RequestPreMoveStealth();

        Assert.True(w.State.IsSneaking);
        Assert.Equal(new[] { "use torch" }, w.Wire);
    }

    // An item cast marks the sneak broken on its `use` and is then followed by the
    // cast path's own re-sneak. One `sn` answers both.
    [Fact]
    public void ItemCast_UseThenCastFired_ReSneaksOnce()
    {
        using World w = new();                      // nothing driving: the in-place re-sneak is live
        w.Feed("Sneaking...");

        w.Send("use waterskin");
        Assert.True(w.Stealth.InPlaceReSneakPendingForTests);
        w.Stealth.ReSneakAfterCast();

        Assert.Equal(new[] { "use waterskin", "sn" }, w.Wire);
        Assert.False(w.Stealth.InPlaceReSneakPendingForTests);
        w.Stealth.ReSneakInPlaceForTests();         // and were it to fire all the same
        Assert.Equal(new[] { "use waterskin", "sn" }, w.Wire);
    }

    // A token, or any item that teleports: the `use` is the move. The sneak is spent
    // as it goes out, so the landing isn't read as a sneak that failed on the way in,
    // and one `sn` takes it again there.
    [Fact]
    public void ItemTeleport_TheUseIsTheMove_LandingReSneaksOnce_AndIsNotALostEntry()
    {
        using World w = new() { EngineDriving = true };
        w.Feed("Sneaking...");
        int lostEntries = 0;
        w.Stealth.SilentSneakLost += () => lostEntries++;

        w.Stealth.RequestPreMoveStealth();          // the walker's hook, ahead of the step
        w.Send("use glowing token");
        w.Stealth.NoteRoomChanged();

        Assert.Equal(new[] { "use glowing token", "sn" }, w.Wire);
        Assert.Equal(0, lostEntries);
        Assert.False(w.Stealth.TakeSneakBrokeOnEntry());
    }

    // ----- the approach hook --------------------------------------------

    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly RoomKey Oasis = new(1, 1);
    private static readonly RoomKey Desert = new(1, 2);

    // The desert room's heat (spell 700) is countered by buff 300, raised with `use
    // waterskin` (item 60).
    private static AutoHazardCounterProvisioner Waterskin(World w)
    {
        Room desert = new()
        {
            Key = Desert, Name = "B", Spell = 700, Exits = new Dictionary<Direction, RoomExit>(),
        };
        RoomHazardIndex.RoomHazard hazard = new(
            new IReadOnlyList<int>[] { new[] { 60 } },
            new[] { new RoomHazardIndex.BuffCounter(300, 712, 600, new[] { 60 }, Array.Empty<int>()) });
        AutoHazardCounterProvisioner hazards = new(
            resolveRoom: key => key == Desert ? desert : null,
            hazardForSpell: spell => spell == 700 ? hazard : null,
            carriedCount: id => id == 60 ? 1 : 0,
            itemName: id => id == 60 ? "waterskin" : null);
        hazards.SetWireSender(w.SendBytes);
        return hazards;
    }

    private (RoomGraphManager Graph, BfsMapper Bfs, RoomTracker Tracker, MovementCoordinator Coordinator) NewMap()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), RoomsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        tracker.SetLocated(Oasis);
        return (graph, new BfsMapper(graph), tracker, new MovementCoordinator());
    }

    // The walker raises the hazard buff as it commits to the step, ahead of the
    // pre-move sneak. The `use` ends the sneak, so the pre-move hook takes it again
    // and the step itself goes out sneaked.
    [Fact]
    public void Walker_StepIntoAHazardRoom_UsesThenSneaksThenMoves()
    {
        using World w = new() { EngineDriving = true };
        var (graph, bfs, tracker, coordinator) = NewMap();
        AutoWalkManager walker = new(graph, bfs, tracker, coordinator);
        AutoHazardCounterProvisioner hazards = Waterskin(w);
        walker.SetWireSender(w.SendBytes);
        walker.SetApproachRoomHook(hazards.OnApproachingRoom);
        walker.SetPreMoveHook(w.Stealth.RequestPreMoveStealth);
        w.Feed("Sneaking...");

        Assert.True(walker.WalkTo(Desert));

        Assert.Equal(new[] { "use waterskin", "sn", "n" }, w.Wire);
    }

    [Fact]
    public void LoopRunner_StepIntoAHazardRoom_UsesThenSneaksThenMoves()
    {
        using World w = new() { EngineDriving = true };
        var (graph, bfs, tracker, coordinator) = NewMap();
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs, postToUi: a => a());
        AutoHazardCounterProvisioner hazards = Waterskin(w);
        runner.SetWireSender(w.SendBytes);
        runner.SetApproachRoomHook(hazards.OnApproachingRoom);
        runner.SetPreMoveHook(w.Stealth.RequestPreMoveStealth);
        w.Feed("Sneaking...");

        Assert.True(runner.Start(new Loop("oasis", new[] { Oasis, Desert })));

        Assert.Equal(new[] { "use waterskin", "sn", "n" }, w.Wire);
    }

    // With the buff still up no `use` goes out, the sneak stands and no `sn` is spent.
    [Fact]
    public void Walker_HazardBuffStillUp_MovesWithoutUseOrSn()
    {
        using World w = new() { EngineDriving = true };
        var (graph, bfs, tracker, coordinator) = NewMap();
        AutoWalkManager walker = new(graph, bfs, tracker, coordinator);
        AutoHazardCounterProvisioner hazards = Waterskin(w);
        walker.SetWireSender(w.SendBytes);
        walker.SetApproachRoomHook(hazards.OnApproachingRoom);
        walker.SetPreMoveHook(w.Stealth.RequestPreMoveStealth);
        hazards.OnApproachingRoom(Desert);          // raised a moment ago
        w.Wire.Clear();
        w.Feed("Sneaking...");

        Assert.True(walker.WalkTo(Desert));

        Assert.Equal(new[] { "n" }, w.Wire);
    }
}
