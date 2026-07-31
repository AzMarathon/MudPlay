using System;
using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Free-vs-direct route comparison: the planner only offers a choice when the
// acquirable-gate route is a genuine shortcut AND the crosser lacks the gate
// item(s). These pin the offer / no-offer boundaries and the requirement
// classification the picker phrases from.
public sealed class RouteChoicePlannerTests
{
    // Direct: 1/1 ──E (Item: 5)── 1/9   (1 hop, gated on a raft).
    // Free:   1/1 ──N── 1/2 ──N── 1/3 ──E── 1/9   (3 hops, gate-free).
    private const string ItemShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The item shortcut is the ONLY route — no gate-free alternative exists.
    private const string ItemOnlyJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Gated and free routes are the SAME length — the "shortcut" saves nothing.
    // Direct: 1/1 ──E (Item: 5)── 1/2 ──E── 1/9   (2 hops).
    // Free:   1/1 ──N── 1/3 ──E── 1/9             (2 hops).
    private const string EqualLengthJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "1/2 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "GateSide",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "FreeSide",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Item shortcut that saves only ONE room — a boat/skiff hop not worth buying.
    // Direct: 1/1 ──E (Item: 5)── 1/9   (1 hop, gated on a boat).
    // Free:   1/1 ──N── 1/2 ──E── 1/9   (2 hops, gate-free).
    private const string ItemShortcutSavesOneJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Same layout as ItemShortcutJson, but the shortcut gate is a Ticket.
    private const string TicketShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/9 (Ticket: 9)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Direct route crosses a pick-only locked door the crosser can't open.
    // Direct: 1/1 ──E (Key: 7 or 80 picklocks)── 1/9   (1 hop).
    // Free:   1/1 ──N── 1/2 ──N── 1/3 ──E── 1/9        (3 hops).
    private const string KeyDoorShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/9 (Key: 7 or 80 picklocks)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Direct route steps THROUGH hazard room 1/5 (Spell 700, countered by item 42).
    // Direct: 1/1 ──E── 1/5 ──E── 1/9              (2 hops, enters the hazard).
    // Free:   1/1 ──N── 1/2 ──N── 1/3 ──E── 1/9    (3 hops, avoids it).
    private const string HazardShortcutRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Hazard", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;
    private const string HazardSpellsJson = """
        [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 } ]
        """;
    private const string HazardItemsJson = """
        [ { "Number": 42, "NegateSpell-0": 700 } ]
        """;

    // The hazard room 1/5 is the ONLY way from 1/1 to 1/9 — no gate-free detour.
    // Direct (and only): 1/1 ──E── 1/5 (Spell 700) ──E── 1/9.
    private const string HazardOnlyRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Hazard", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A CMD>0 room with an "(Item: N)" exit is the teleport pattern: TryReadRoom
    // promotes that exit to RoomExitHint.Teleport, which BFS crosses as a normal
    // short edge. Here 1/1's E teleports straight to 1/9 (1 hop), while the
    // walking route detours 1/1→1/2→1/3→1/9 (3 hops).
    private const string TeleportShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "CMD": 5,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The teleport is the ONLY way from 1/1 to 1/9 — no walking route exists.
    private const string TeleportOnlyJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "CMD": 5,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Teleport saves only one room over the walk — below the picker's floor.
    // Teleport: 1/1 ──E(teleport)── 1/9   (1 hop).
    // Walk:     1/1 ──N── 1/2 ──E── 1/9   (2 hops).
    private const string TeleportSavesOneJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "CMD": 5,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // No teleport anywhere — the shortest route already walks the whole way.
    private const string NoTeleportJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The teleport fixtures all put CMD 5 on 1/1 with an (Item: 5) exit to 1/9; the
    // Item→Teleport promotion is TBInfo-gated, so a CMD-5 chain that teleports to
    // 1/9 makes the exit legitimately promote.
    private const string TeleportTbInfo =
        """[ { "Number": 5, "Action": "go arch:teleport 9 1\n" } ]""";

    private static void WithTeleportGraph(
        string roomsJson,
        Action<BfsMapper, RoomGraphManager, MovementFilter> body)
        => WithGraph(roomsJson, body, tbInfoJson: TeleportTbInfo);

    private static void WithGraph(
        string roomsJson,
        Action<BfsMapper, RoomGraphManager, MovementFilter> body,
        string? spellsJson = null,
        string? itemsJson = null,
        Action<RoomHazardIndex, MovementFilter>? wireHazards = null,
        string? tbInfoJson = null)
    {
        string root = Path.Combine(Path.GetTempPath(),
            "fujinterm-routechoice-" + Path.GetRandomFileName());
        try
        {
            string setDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "Rooms.json"), roomsJson);
            if (spellsJson is not null) File.WriteAllText(Path.Combine(setDir, "Spells.json"), spellsJson);
            if (itemsJson is not null) File.WriteAllText(Path.Combine(setDir, "Items.json"), itemsJson);
            if (tbInfoJson is not null) File.WriteAllText(Path.Combine(setDir, "TBInfo.json"), tbInfoJson);

            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            // The Item→Teleport promotion is TBInfo-gated: build with a TBInfoStore
            // when a teleport CMD chain is supplied so the fixture's (Item: N) exit
            // legitimately promotes.
            RoomGraphManager graph;
            if (tbInfoJson is not null)
            {
                TBInfoStore tbinfo = new(cache);
                tbinfo.OnActiveSetChanged("alpha");
                graph = new(cache, log: null, tbinfo);
            }
            else
            {
                graph = new(cache);
            }
            graph.OnActiveSetChanged("alpha");
            BfsMapper bfs = new(graph);

            ProfileService profile = new();
            profile.LoadBlank();
            MovementFilter filter = new(profile);

            if (wireHazards is not null)
            {
                RoomHazardIndex index = new(cache);
                index.OnActiveSetChanged("alpha");
                wireHazards(index, filter);
            }

            body(bfs, graph, filter);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void OffersChoice_WhenItemShortcutIsShorter_AndItemMissing()
    {
        WithGraph(ItemShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // lacking the raft

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(3, choice!.FreeStepCount);
            Assert.Equal(1, choice.GatedStepCount);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.CarryItem, req.Kind);
            Assert.Equal(new[] { 5 }, req.ItemIds);

            // The picker previews each route as a RoomKey line (source first,
            // then every hop's target) — free detours 1/1→1/2→1/3→1/9, the
            // direct hop is 1/1→1/9.
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3), new RoomKey(1, 9) },
                choice.FreePath);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 9) },
                choice.GatedPath);
        });
    }

    [Fact]
    public void NoChoice_WhenItemCarried_RoutesCoincide()
    {
        WithGraph(ItemShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = id => id == 5;   // already holding the raft

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // Free route already takes the direct hop, so gated == free → no offer.
            Assert.Null(choice);
        });
    }

    [Fact]
    public void OffersSoleItemRoute_WhenNoFreeAlternative()
    {
        WithGraph(ItemOnlyJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // The item gate is the only way through — surface it (HasFreeRoute
            // false) so the caller's flag logic decides whether to arm the
            // acquisition pipeline or fail in place naming the item.
            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);
            Assert.Empty(choice.FreePath);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.CarryItem, req.Kind);
        });
    }

    [Fact]
    public void NoChoice_WhenShortcutSavesNoSteps()
    {
        WithGraph(EqualLengthJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.Null(choice);   // gated 2 hops, free 2 hops — no bargain
        });
    }

    [Fact]
    public void NoChoice_WhenItemShortcutSavesOnlyOneStep()
    {
        WithGraph(ItemShortcutSavesOneJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // lacking the boat

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // Gated 1 hop, free 2 hops — a single room saved isn't worth buying
            // the boat, so no picker; the caller just walks the free route.
            Assert.Null(choice);
        });
    }

    [Fact]
    public void ClassifiesTicketRequirement()
    {
        WithGraph(TicketShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            RouteRequirement req = Assert.Single(choice!.Requirements);
            Assert.Equal(RouteRequirementKind.Ticket, req.Kind);
            Assert.Equal(new[] { 9 }, req.ItemIds);
        });
    }

    [Fact]
    public void ClassifiesDoorKeyRequirement()
    {
        WithGraph(KeyDoorShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;          // key not held
            filter.StrengthProvider = () => 10;
            filter.PicklocksProvider = () => 0;            // can't pick statReq 80
            filter.MaxBashableStrengthProvider = () => 200;

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            RouteRequirement req = Assert.Single(choice!.Requirements);
            Assert.Equal(RouteRequirementKind.DoorKey, req.Kind);
            Assert.Equal(new[] { 7 }, req.ItemIds);
        });
    }

    [Fact]
    public void ClassifiesHazardProtectionRequirement()
    {
        WithGraph(HazardShortcutRoomsJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(3, choice!.FreeStepCount);
            Assert.Equal(2, choice.GatedStepCount);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(new[] { 42 }, req.ItemIds);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 5) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // no counter
        });
    }

    // No gate-free route AND the only path crosses a survivable hazard → offer
    // the direct route (HasFreeRoute false) so the walk can carry / buy / `use`
    // the counter instead of aborting with "a room hazard you can't survive".
    [Fact]
    public void OffersHazardOnlyRoute_WhenNoFreeAlternative()
    {
        WithGraph(HazardOnlyRoomsJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);        // every path crosses the hazard
            Assert.Empty(choice.FreePath);
            Assert.Equal(2, choice.GatedStepCount);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(new[] { 42 }, req.ItemIds);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 5), new RoomKey(1, 9) },
                choice.GatedPath);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 5) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // no counter
        });
    }

    // ----- EvaluateTeleport: walk-vs-teleport fork -------------------

    [Fact]
    public void OffersTeleportChoice_WhenTeleportShortcuts_AndWalkExists()
    {
        WithTeleportGraph(TeleportShortcutJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTeleport(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(RouteChoiceKind.Teleport, choice!.Kind);
            Assert.Equal(3, choice.FreeStepCount);      // walking route
            Assert.Equal(1, choice.GatedStepCount);     // teleport hop
            Assert.Empty(choice.Requirements);          // no item gate on a teleport
            Assert.NotNull(choice.TeleportLanding);
            Assert.Contains("Vault", choice.TeleportLanding!);

            // Free path is the pure-walking detour; gated path is the teleport hop.
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3), new RoomKey(1, 9) },
                choice.FreePath);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 9) },
                choice.GatedPath);
        });
    }

    [Fact]
    public void NoTeleportChoice_WhenNoTeleportOnRoute()
    {
        WithGraph(NoTeleportJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTeleport(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // Shortest route already walks the whole way — nothing to weigh.
            Assert.Null(choice);
        });
    }

    [Fact]
    public void NoTeleportChoice_WhenTeleportIsOnlyRoute()
    {
        WithTeleportGraph(TeleportOnlyJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTeleport(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // No walking alternative — there's no fork, so the walker just takes
            // the teleport rather than prompting.
            Assert.Null(choice);
        });
    }

    [Fact]
    public void NoTeleportChoice_WhenTeleportSavesOnlyOneStep()
    {
        WithTeleportGraph(TeleportSavesOneJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTeleport(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // Teleport 1 hop, walk 2 hops — a single room saved isn't worth
            // interrupting the walk to weigh against the teleport's danger.
            Assert.Null(choice);
        });
    }

    // The refuseTeleports pass backs the picker's "walk it" choice: the default
    // pass takes the teleport as the shortest route; refusing it returns the
    // pure-walking route.
    [Fact]
    public void Bfs_RefuseTeleports_ReturnsWalkingRouteNotTeleport()
    {
        WithTeleportGraph(TeleportShortcutJson, (bfs, graph, _) =>
        {
            IReadOnlyList<Direction>? tele = bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 9), null);
            Assert.NotNull(tele);
            Assert.Single(tele!);   // the 1-hop teleport

            IReadOnlyList<Direction>? walk = bfs.FindPath(
                new RoomKey(1, 1), new RoomKey(1, 9), null, refuseTeleports: true);
            Assert.NotNull(walk);
            Assert.Equal(3, walk!.Count);   // the 3-hop walking detour
        });
    }
}
