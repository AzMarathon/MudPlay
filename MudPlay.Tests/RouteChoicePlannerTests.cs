using System;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

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

    // A hidden exit whose unlock action needs a held item. The action rides a
    // SIBLING direction cell, exactly as the MegaMUD export writes it — the Lower
    // Caverns bloodstone-orb exit puts its S-exit action in the room's unused N
    // cell — so the requirement lives in MultiAction, not KeyItemId.
    // Direct: 1/1 ──S (hidden, `rub orb`, Item: 5)── 1/9   (1 hop).
    // Free:   1/1 ──E── 1/2 ──E── 1/3 ──E── 1/9            (3 hops, gate-free).
    private const string MultiActionItemShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Underground Lake",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "Action [on the S exit of this room]: rub orb (Item: 5)",
            "S": "1/9 (Hidden/Needs 1 Actions, any order)", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/3", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Narrow Passage",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A plain door the crosser can't open walls off the ONLY route to 1/9. Not an
    // acquirable gate (no key), so suspending acquirable gates doesn't open it —
    // Evaluate returns null, and PlanBlocked offers "run to the blocked room".
    // Only route: 1/1 ──E── 1/2 ──E (Door [200 picklocks])── 1/9.
    private const string PlainDoorOnlyJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Antechamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9 (Door [200 picklocks])", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
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

    // Dest 1/9 sits BEHIND hazard room 1/5 (its only approach); a shop room 1/2
    // hangs off the gate-free side. Sourcing a counter at the shop means scoring
    // dist(cur→shop→dest), whose dest leg passes THROUGH the hazard — reachable
    // only with the acquirable gates suspended (counter in hand).
    private const string ShopBehindHazardRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Shop", "Spell": 0,
            "Light": 0, "Shop": 3, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
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
            "mudplay-routechoice-" + Path.GetRandomFileName());
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

    // The paradigm-20260913-100733 shape: the only way to 1/9 crosses a REQUIRED orb
    // gate (807), after which two routes diverge — a shorter one through an OPTIONAL
    // amber-talisman shortcut (815) and a longer one through a gate-key door (806) the
    // crosser already holds. The planner must commit the long, reliable route (so its
    // carried key surfaces), report only the orb as required, and offer the talisman as
    // an optional shortcut — never call it "required".
    // Shortest (all gates suspended): 1/1 ─E(orb)─ 1/2 ─E(talisman)─ 1/9   (2 hops).
    // Committed (talisman avoided):    1/1 ─E(orb)─ 1/2 ─N─ 1/3 ─E(key)─ 1/9 (3 hops).
    private const string OptionalShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Bank",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2 (Item: 807)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Fork",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "1/9 (Item: 815)", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "CityGate",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9 (Key: 806)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void SoleRoute_OptionalShortcut_IsNotRequired_AndHeldKeySurfaces()
    {
        WithGraph(OptionalShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = id => id == 806;   // holds the gate key, lacks orb + talisman

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);
            // Committed to the reliable long route (3 hops), not the talisman shortcut (2).
            Assert.Equal(3, choice.GatedStepCount);

            // The orb is genuinely required; the held gate key surfaces as carried.
            RouteRequirement orb = Assert.Single(choice.Requirements,
                r => r.Kind == RouteRequirementKind.CarryItem && r.ItemIds.SequenceEqual(new[] { 807 }));
            Assert.False(orb.Optional);
            Assert.False(orb.Carried);
            RouteRequirement key = Assert.Single(choice.Requirements,
                r => r.Kind == RouteRequirementKind.DoorKey && r.ItemIds.SequenceEqual(new[] { 806 }));
            Assert.True(key.Carried);
            // The amber talisman is NEVER a requirement.
            Assert.DoesNotContain(choice.Requirements, r => r.ItemIds.Contains(815));

            // It's surfaced as an optional shortcut instead, saving the one room.
            Assert.Equal(new[] { 815 }, choice.ShortcutItems);
            Assert.Equal(2, choice.ShortcutStepCount);

            // Acquisition sources the orb but never the carried key or the shortcut item.
            Assert.Equal(new[] { 807 }, RouteChoicePlanner.SourceableGateItems(choice.Requirements, NoSummons));
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

    // The gate that went unreported: a hidden exit's held-item requirement used to
    // fall through Classify to HazardRequirement, return null, and vanish — so the
    // picker offered the shortcut without ever naming the item the walk would need
    // (report paradigm-20260911-010954).
    [Fact]
    public void ClassifiesMultiActionHeldItemRequirement()
    {
        WithGraph(MultiActionItemShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // lacking the orb

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            RouteRequirement req = Assert.Single(choice!.Requirements);
            Assert.Equal(RouteRequirementKind.CarryItem, req.Kind);
            Assert.Equal(new[] { 5 }, req.ItemIds);
        });
    }

    [Fact]
    public void NoChoice_WhenMultiActionHeldItemCarried()
    {
        WithGraph(MultiActionItemShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = id => id == 5;   // orb in hand

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // The hidden exit is already crossable, so the free route takes it —
            // gated == free, nothing to offer.
            Assert.Null(choice);
        });
    }

    // Fully blocked by a non-acquirable door → Evaluate offers nothing, but
    // PlanBlocked walks to the room just before it.
    [Fact]
    public void PlanBlocked_PlainDoorWallsOnlyRoute_StopsBeforeIt()
    {
        WithGraph(PlainDoorOnlyJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
            filter.StrengthProvider = () => 10;
            filter.PicklocksProvider = () => 0;            // can't pick statReq 200
            filter.MaxBashableStrengthProvider = () => 200;

            // Evaluate offers nothing (a plain door isn't acquirable).
            Assert.Null(RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));

            BlockedRoutePlan? plan = RouteChoicePlanner.PlanBlocked(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(plan);
            Assert.Equal(new RoomKey(1, 2), plan!.StopRoom);       // room just before the door
            Assert.Equal(Direction.E, plan.BlockDir);
            Assert.Equal(new RoomKey(1, 9), plan.BlockExit.Target);
            Assert.Equal(new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, plan.Preview);
        });
    }

    [Fact]
    public void PlanBlocked_ReachableRoute_ReturnsNull()
    {
        WithGraph(ItemShortcutJson, (bfs, graph, filter) =>
            // The free (gate-free) route to 1/9 is open, so there's nothing to run up to.
            Assert.Null(RouteChoicePlanner.PlanBlocked(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9))));
    }

    [Fact]
    public void PlanBlocked_BlockedAtDoorstep_ReturnsNull()
    {
        // 1/1 ──E (Door [200 picklocks])── 1/9: the block is the very first hop, so
        // there's no reachable room to run to short of it.
        const string doorstepJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Start",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "1/9 (Door [200 picklocks])", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 9, "Name": "Vault",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "1/1",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        WithGraph(doorstepJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
            filter.StrengthProvider = () => 10;
            filter.PicklocksProvider = () => 0;
            filter.MaxBashableStrengthProvider = () => 200;

            Assert.Null(RouteChoicePlanner.PlanBlocked(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));
        });
    }

    [Fact]
    public void PlanBlocked_LevelBlockedDestination_NamesTheLevelGate_NotAnImpassableDoorDetour()
    {
        // The Ancient Fortress shape: the destination (1/9) is reachable only past
        // a level gate a low-level character fails — but the graph ALSO "reaches"
        // it through an impassable 1000-picklock door (the pyramid door / pit
        // drops). PlanBlocked must name the LEVEL gate, not the incidental door,
        // so the picker tells the user WHY (they're not high enough level).
        const string fortressJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Approach",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/8", "E": "1/2", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "Gatehouse",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "1/9 (Level: 75 to 999)", "W": "1/1",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 8, "Name": "Pyramid",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/1", "S": "0", "E": "1/9 (Door [1000 picklocks/strength])", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 9, "Name": "Jailer Room",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "1/2",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        WithGraph(fortressJson, (bfs, graph, filter) =>
        {
            filter.LevelProvider = () => 28;               // under the level-75 gate
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
            filter.StrengthProvider = () => 10;
            filter.PicklocksProvider = () => 0;            // can't pick the 1000-door
            filter.MaxBashableStrengthProvider = () => 200;

            BlockedRoutePlan? plan = RouteChoicePlanner.PlanBlocked(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(plan);
            Assert.True(plan!.BlockExit.HasLevelGate,
                "the named block must be the level gate, not the impassable door");
            Assert.Equal(75, plan.BlockExit.MinLevel);
            Assert.Equal(new RoomKey(1, 2), plan.StopRoom);
        });
    }

    [Fact]
    public void PlanBlocked_LevelGatedTeleportShortcut_NamesTheDestinationsOwnLevelGate_NotTheShortcut()
    {
        // Redstone-Tunnel shape: the destination (1/9) is reachable by a LAND route
        // through a level-75 gate, OR by a level-40 teleport shortcut. A blocked
        // walker routes around the shortcut, so the reported block must be the
        // destination's own level-75 gate — not the level-40 portal it can't and
        // wouldn't take (report paradigm-20260914-201123).
        const string rooms = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Fork", "CMD": 6,
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/2", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "Approach",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/1", "S": "0", "E": "1/9 (Level: 75 to 999)", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 9, "Name": "Destination",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "1/2",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        // CMD 6 on 1/1 synthesises a level-40 teleport STRAIGHT to 1/9 (1 hop) — a
        // shorter shortcut than the 2-hop land route, so without refusing teleports
        // the physical route takes it and the block would read as the level-40
        // portal instead of the destination's level-75 gate.
        const string tbInfo = """[ { "Number": 6, "Action": "go portal:minlevel 40:teleport 9 1\n" } ]""";

        WithGraph(rooms, (bfs, graph, filter) =>
        {
            filter.LevelProvider = () => 28;               // under both gates
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
            filter.StrengthProvider = () => 10;
            filter.PicklocksProvider = () => 0;
            filter.MaxBashableStrengthProvider = () => 200;

            // Guard the fixture: the level-40 teleport shortcut really is synthesised.
            Assert.True(graph.GetRoom(new RoomKey(1, 1))!.Exits
                            .TryGetValue(Direction.Teleport, out RoomExit tp) && tp.MinLevel == 40,
                "fixture no longer produces the level-40 teleport shortcut");

            BlockedRoutePlan? plan = RouteChoicePlanner.PlanBlocked(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(plan);
            Assert.Equal(75, plan!.BlockExit.MinLevel);    // the destination's gate, NOT the level-40 portal
            Assert.Equal(new RoomKey(1, 2), plan.StopRoom);
        }, tbInfoJson: tbInfo);
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

    //  1/1 ──E(Trap)── 1/9 (Spell 700, hazard)   trapped 1-hop approach
    //   │               │
    //   S               E (from 1/2, clean)
    //   └──── 1/2 ──────┘                         trap-free 2-hop approach
    // The hazard gates EVERY entry to 1/9, so there's no free route — but one
    // approach is trapped and the other isn't (report paradigm-20260825-125954,
    // room 17/808: manhole-gated, one exit also a 250-damage trap).
    private const string HazardTrapApproachRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9 (Trap)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Detour", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "0", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Hazard Vault", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void HazardSoleRoute_PrefersTheTrapFreeApproach()
    {
        // The reported compound case: the destination is hazard-gated on every side
        // (no free route), but one approach also crosses a trap. The forced hazard
        // crossing must take the fewest-traps approach — the longer, trap-free one —
        // not the shortest-by-hops approach that eats the trap.
        WithGraph(HazardTrapApproachRoomsJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);
            Assert.Equal(2, choice.GatedStepCount);     // the trap-free detour, not the 1-hop trap
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 9) },
                choice.GatedPath);
            Assert.Equal(0, bfs.CountTrapsOnPath(new RoomKey(1, 1),
                new[] { Direction.S, Direction.E }));    // chosen route crosses no trap
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);   // still the manhole gate
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 9) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // no counter
        });
    }

    // The hazard-counter shop resolver scores dist(cur→shop→dest), but the dest
    // sits behind the very hazard the counter answers — so no shop qualifies with
    // the hazard live. Guards ResolveHazardCounter's suspend scope: without it a
    // buyable counter is silently never offered (the FCCO "buy a rope" bug).
    [Fact]
    public void HazardCounterShop_SelectableOnlyWithGatesSuspended()
    {
        WithGraph(ShopBehindHazardRoomsJson, (bfs, graph, filter) =>
        {
            Func<RoomKey, RoomKey, int?> dist = (a, b) => bfs.DistanceBetween(a, b, filter);
            RoomKey[] shops = { new(1, 2) };

            // Hazard live: the dest is unreachable from the shop, so nothing scores.
            Assert.False(PathItemShopRouter.TrySelectShop(
                shops, new RoomKey(1, 1), new RoomKey(1, 9), dist, out _));

            // Gates suspended (counter in hand): the round-trip resolves, shop wins.
            using (filter.SuspendAcquirableGates())
            {
                Assert.True(PathItemShopRouter.TrySelectShop(
                    shops, new RoomKey(1, 1), new RoomKey(1, 9), dist, out RoomKey shop));
                Assert.Equal(new RoomKey(1, 2), shop);
            }
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 5) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // no counter carried
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

    // ----- EvaluateTrapAvoid: avoid-traps fork -----------------------
    //
    //  1/1 ──N (Trap)── 1/2 (goal)   trapped 1-hop shortcut
    //   │                │
    //   E                W
    //   └──── 1/3 ──N────┘           clean 2-hop detour
    //
    private const string TrapForkJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Fork",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Trap)", "S": "0", "E": "1/3", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Detour",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // No trap anywhere — a plain two-hop corridor.
    private const string NoTrapJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The trapped exit is the ONLY way to the goal — no clean alternative.
    private const string TrapOnlyJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Trap)", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    //  1/1 ──N(Trap)── 1/2 ──N(Trap)── 1/4 (goal)   shortest 2 traps; the 1/2→1/4
    //   └──── E,1/3 ──N────┘                         trap is unavoidable, the other isn't
    private const string MultiTrapJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Fork",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Trap)", "S": "0", "E": "1/3", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4 (Trap)", "S": "1/1", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Detour",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void OffersTrapAvoidChoice_WhenShortestCrossesTrap_AndCleanRouteExists()
    {
        WithGraph(TrapForkJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTrapAvoid(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 2));

            Assert.NotNull(choice);
            Assert.Equal(RouteChoiceKind.TrapAvoid, choice!.Kind);
            Assert.Equal(2, choice.FreeStepCount);      // trap-free detour
            Assert.Equal(1, choice.GatedStepCount);     // trapped shortcut
            Assert.Equal(0, choice.FreeTrapCount);      // detour is fully clean
            Assert.Equal(1, choice.GatedTrapCount);     // shortcut crosses the trap
            Assert.Empty(choice.Requirements);
            Assert.True(choice.HasFreeRoute);

            // Free path is the clean detour; gated path is the trapped shortcut.
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 3), new RoomKey(1, 2) },
                choice.FreePath);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 2) },
                choice.GatedPath);
        });
    }

    [Fact]
    public void OffersTrapAvoidChoice_WhenFewerButNotZeroTraps()
    {
        // The reported case: a route with an avoidable AND an unavoidable trap. The
        // fork must still fire, offering the fewest-traps route (1 trap) over the
        // shortest (2 traps) — not give up because it can't be made fully clean.
        WithGraph(MultiTrapJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTrapAvoid(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 4));

            Assert.NotNull(choice);
            Assert.Equal(RouteChoiceKind.TrapAvoid, choice!.Kind);
            Assert.Equal(3, choice.FreeStepCount);      // longer detour
            Assert.Equal(2, choice.GatedStepCount);     // shortest
            Assert.Equal(1, choice.FreeTrapCount);      // dodged one, kept the unavoidable
            Assert.Equal(2, choice.GatedTrapCount);     // shortest crosses both
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 3), new RoomKey(1, 2), new RoomKey(1, 4) },
                choice.FreePath);
        });
    }

    [Fact]
    public void NoTrapAvoidChoice_WhenRouteCrossesNoTrap()
    {
        WithGraph(NoTrapJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTrapAvoid(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 2));

            // Nothing to weigh — the shortest route is already trap-free.
            Assert.Null(choice);
        });
    }

    [Fact]
    public void NoTrapAvoidChoice_WhenTrapIsUnavoidable()
    {
        WithGraph(TrapOnlyJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTrapAvoid(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 2));

            // The fewest-traps route crosses the same one trap as the shortest — it
            // can't dodge any, so there's nothing better to offer. Walker crosses and
            // disarms, no fork.
            Assert.Null(choice);
        });
    }

    // ----- Teleport / TrapAvoid forks defer to an obtainable shortcut ----
    //
    // The pre-Evaluate forks weigh only gate-honoured routes; without a deferral they
    // preempt Evaluate's obtain/cross even when buying a boat opens a far shorter route
    // (the same gap the avoid-override fork had — report paradigm-20260915-182554).

    // Teleport route (1/1 =tele=> 1/5 -E- 1/6 -E- 1/9, 3 hops) vs pure walk (1/1-N-1/2
    // -N-1/3-N-1/4-N-1/7-N-1/9, 5 hops) — teleport fires. But a boat opens 1/1-S-1/8
    // (River 700)-S-1/9 (2 hops), shorter than the teleport. 1/5 is teleport-only.
    private const string TeleportObtainBypassJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "CMD": 5,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/8", "E": "1/5 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Landing", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/6", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "L2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "W1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "W2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "W3", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/7", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 7, "Name": "W4", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "1/4", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 8, "Name": "River", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "1/9", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/8", "S": "1/7", "E": "0", "W": "1/6",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // CMD-5 chain teleports to 1/5 (matching the (Item: 5) exit target so it promotes).
    private const string TeleportTo5TbInfo =
        """[ { "Number": 5, "Action": "go arch:teleport 5 1\n" } ]""";

    [Fact]
    public void NoTeleportChoice_WhenAnObtainableRouteBeatsTheTeleport()
    {
        WithGraph(TeleportObtainBypassJson, (bfs, graph, filter) =>
        {
            // The teleport would otherwise be offered (3 hops vs a 5-hop walk), but a
            // boat opens a 2-hop river route shorter than the teleport → defer.
            Assert.Null(RouteChoicePlanner.EvaluateTeleport(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));
            Assert.NotNull(choice);
            RouteRequirement req = Assert.Single(choice!.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(new[] { 42 }, req.ItemIds);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 8), new RoomKey(1, 9) },
                choice.GatedPath);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        tbInfoJson: TeleportTo5TbInfo,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 8) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
        });
    }

    // Trapped shortest (1/1-E(Trap)-1/5-E-1/6-E-1/9, 3 hops) vs a 4-hop trap-free
    // detour — trap-avoid fires. But a boat opens a 2-hop trap-free river route.
    private const string TrapObtainBypassJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/8", "E": "1/5 (Trap)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "T1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/6", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "T2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "D1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "D2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D3", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 8, "Name": "River", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "1/9", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/8", "S": "1/4", "E": "0", "W": "1/6",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void NoTrapAvoidChoice_WhenAnObtainableTrapFreeRouteIsShorter()
    {
        WithGraph(TrapObtainBypassJson, (bfs, graph, filter) =>
        {
            // Trap-avoid would otherwise fire (3-hop trapped vs 4-hop clean detour), but
            // a boat opens a 2-hop trap-free river route → defer to Evaluate.
            Assert.Null(RouteChoicePlanner.EvaluateTrapAvoid(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));
            Assert.NotNull(choice);
            RouteRequirement req = Assert.Single(choice!.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 8), new RoomKey(1, 9) },
                choice.GatedPath);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 8) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
        });
    }

    // TrapObtainBypass, but the river crossing (1/8 → 1/9) is itself trapped, so the
    // boat route isn't clean — the trap-avoid deferral must NOT fire.
    private const string TrapObtainBypassTrappedRiverJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/8", "E": "1/5 (Trap)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "T1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/6", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "T2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "D1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "D2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D3", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 8, "Name": "River", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "1/9 (Trap)", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/8", "S": "1/4", "E": "0", "W": "1/6",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Guard: the trap-avoid deferral must NOT fire when the obtainable route still
    // crosses a trap — the trap warning has to stand.
    [Fact]
    public void KeepsTrapAvoidChoice_WhenTheObtainableRouteAlsoTraps()
    {
        WithGraph(TrapObtainBypassTrappedRiverJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTrapAvoid(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));
            Assert.NotNull(choice);   // boat route is trapped too → trap-avoid still offered
            Assert.Equal(RouteChoiceKind.TrapAvoid, choice!.Kind);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 8) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
        });
    }

    // ----- EvaluateAvoidOverride: route-through-avoided-rooms fork ----
    //
    // 1/1 ──E── 1/5 ──E── 1/9   the ONLY route to 1/9 runs through 1/5.
    private const string AvoidSoleJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Avoided",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A 2-hop route through avoided 1/5 vs a 4-hop avoid-free detour (saves 2).
    // 1/1 ──E── 1/5 ──N── 1/9   AND   1/1 ──N── 1/2 ──N── 1/3 ──E── 1/4 ──E── 1/9
    private const string AvoidTwoRouteJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Avoided",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Mid3",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/5", "E": "0", "W": "1/4",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A 2-hop route through avoided 1/5 vs a 3-hop avoid-free detour (saves only 1).
    // 1/1 ──E── 1/5 ──N── 1/9   AND   1/1 ──N── 1/2 ──E── 1/3 ──E── 1/9
    private const string AvoidTwoRouteSmallJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Avoided",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/3", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/5", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void OffersSoleAvoidOverride_WhenAvoidWallsTheOnlyRoute()
    {
        WithGraph(AvoidSoleJson, (bfs, graph, filter) =>
        {
            filter.MarkAvoided(new RoomKey(1, 5));   // the only path room, marked avoid

            RouteChoice? choice = RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(RouteChoiceKind.AvoidOverride, choice!.Kind);
            Assert.False(choice.HasFreeRoute);          // no avoid-honouring route
            Assert.Empty(choice.FreePath);
            Assert.Equal(2, choice.GatedStepCount);
            Assert.Equal(1, choice.AvoidedRoomCount);   // routes through 1 avoided room
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 5), new RoomKey(1, 9) },
                choice.GatedPath);
        });
    }

    [Fact]
    public void OffersTwoRouteAvoidOverride_WhenAvoidRouteIsMuchShorter()
    {
        WithGraph(AvoidTwoRouteJson, (bfs, graph, filter) =>
        {
            filter.MarkAvoided(new RoomKey(1, 5));

            RouteChoice? choice = RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(RouteChoiceKind.AvoidOverride, choice!.Kind);
            Assert.True(choice.HasFreeRoute);           // an avoid-honouring route exists
            Assert.Equal(4, choice.FreeStepCount);      // the avoid-free detour
            Assert.Equal(2, choice.GatedStepCount);     // the shorter route through 1/5
            Assert.Equal(1, choice.AvoidedRoomCount);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 5), new RoomKey(1, 9) },
                choice.GatedPath);
        });
    }

    [Fact]
    public void NoAvoidOverride_WhenAvoidRouteSavesTooLittle()
    {
        WithGraph(AvoidTwoRouteSmallJson, (bfs, graph, filter) =>
        {
            filter.MarkAvoided(new RoomKey(1, 5));

            // Through-avoid saves only 1 room over the avoid-free route — the user's
            // deliberate avoid stands, no override offered.
            Assert.Null(RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));
        });
    }

    [Fact]
    public void NoAvoidOverride_WhenRouteTouchesNoAvoidedRoom()
    {
        WithGraph(AvoidTwoRouteJson, (bfs, graph, filter) =>
            // Nothing marked avoid → the shortest route touches no avoided room, so
            // there's no override to offer.
            Assert.Null(RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9))));
    }

    // Report paradigm-20260907-212758: the ONLY avoid-respecting route crosses a
    // hazard room (a river you'd cross with a bought raft); the only avoid-FREE
    // route runs through a marked-avoid room. The picker must NOT claim "no route
    // respects your avoids" — an obtainable counter opens an avoid-respecting route,
    // so EvaluateAvoidOverride defers and Evaluate surfaces the obtain/cross options.
    //   avoid-respecting: 1/1 ──N── 1/2 (River, Spell 700, counter 42) ──N── 1/9
    //   avoid-crossing:   1/1 ──E── 1/5 (Avoided) ──E── 1/9
    private const string AvoidWithHazardBypassJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "River", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Avoided", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void AvoidOverride_DefersWhenAnAcquirableCounterOpensAnAvoidRespectingRoute()
    {
        WithGraph(AvoidWithHazardBypassJson, (bfs, graph, filter) =>
        {
            filter.MarkAvoided(new RoomKey(1, 5));   // the only avoid-FREE route runs through here

            // The avoid-override must NOT fire: a raft (item 42) opens the river route,
            // which respects the avoid. Deferred so the item-gate fork handles it.
            Assert.Null(RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));

            // And Evaluate surfaces that river crossing as a sole hazard route
            // (obtain / cross-unprotected), respecting the avoid.
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));
            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(new[] { 42 }, req.ItemIds);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 9) },
                choice.GatedPath);   // the avoid-respecting river route, not through 1/5

            // The avoid-crossing route (through 1/5) is available as the EXTRA card:
            // it needs no counter, so it's a real alternative to the raft crossing.
            var alt = RouteChoicePlanner.AvoidAlternative(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));
            Assert.NotNull(alt);
            Assert.Equal(1, alt!.Value.AvoidedCount);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 5), new RoomKey(1, 9) },
                alt.Value.Path);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 2) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // no raft carried
        });
    }

    // Report paradigm-20260915-182554 (the TWO-ROUTE twin of the sole case above):
    // an avoid-honouring route EXISTS (a long detour), and a shorter route through a
    // marked-avoid room exists — but OBTAINING a boat opens an even shorter avoid-
    // respecting river crossing. The two-route avoid-override must defer so Evaluate
    // offers "buy a boat and sail" instead of nagging about the avoid.
    //   detour (avoid + gate honoured, 5 hops): 1/1-N-1/2-N-1/3-N-1/4-N-1/6-N-1/9
    //   avoid-crossing  (3 hops):               1/1-E-1/5(Avoided)-E-1/7-E-1/9
    //   boat (hazard, 2 hops, gate-suspended):  1/1-S-1/8(River 700)-S-1/9
    private const string AvoidTwoRouteWithHazardBypassJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/8", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "D1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "D2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D3", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/6", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "D4", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "1/4", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Avoided", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/7", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 7, "Name": "AvoidMid", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 8, "Name": "River", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "1/9", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/8", "S": "1/6", "E": "0", "W": "1/7",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void NoTwoRouteAvoidOverride_WhenAnAcquirableCounterOpensAShorterAvoidRespectingRoute()
    {
        WithGraph(AvoidTwoRouteWithHazardBypassJson, (bfs, graph, filter) =>
        {
            filter.MarkAvoided(new RoomKey(1, 5));   // the avoid-crossing shortcut runs through here

            // Two-route avoid-override would otherwise fire (5-hop detour vs 3-hop
            // through-avoid, saves 2) — but a boat opens a 2-hop avoid-respecting river
            // route, so it must defer to Evaluate instead of offering the override.
            Assert.Null(RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));

            // Evaluate surfaces the river crossing (obtain boat / cross), avoid-respecting.
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));
            Assert.NotNull(choice);
            RouteRequirement req = Assert.Single(choice!.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(new[] { 42 }, req.ItemIds);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 8), new RoomKey(1, 9) },
                choice.GatedPath);   // the river route, not through avoided 1/5
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 8) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // no boat carried
        });
    }

    [Fact]
    public void AvoidAlternative_IsNull_WhenNothingIsMarkedAvoid()
    {
        WithGraph(AvoidWithHazardBypassJson, (bfs, graph, filter) =>
            // Nothing avoided → the ignore-avoids route touches no marked room, so
            // there's no extra avoid-crossing card to offer.
            Assert.Null(RouteChoicePlanner.AvoidAlternative(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9))));
    }
    // ----- Sourceable gate items (what an "obtain then cross" pick arms) -------
    //
    // Report paradigm-20260911-095404: the pick force-obtained only hazard
    // counters, so an item-gated route armed nothing whenever Settings → Other →
    // "search rooms if item needed" was off — the walk then crossed the Lower
    // Caverns gate having made no arrangements, rubbed an orb it did not carry,
    // and bonked.

    private static readonly Func<int, bool> NoSummons = _ => false;
    private static readonly Func<int, bool> AllSummonable = _ => true;

    [Fact]
    public void SourceableGateItems_IncludesCarryItemsAndTickets()
    {
        var reqs = new[]
        {
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 807 }),
            new RouteRequirement(RouteRequirementKind.Ticket, new[] { 9 }),
        };
        Assert.Equal(new[] { 807, 9 }, RouteChoicePlanner.SourceableGateItems(reqs, NoSummons));
    }

    // The picker resolves hazard counters itself (it may grab one off the current
    // floor instead of detouring) and forces them on its own path.
    [Fact]
    public void SourceableGateItems_ExcludesHazardCounters()
    {
        var reqs = new[]
        {
            new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 42, 43 }),
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 807 }),
        };
        Assert.Equal(new[] { 807 }, RouteChoicePlanner.SourceableGateItems(reqs, NoSummons));
    }

    // A door key is only worth arming for when a room command can summon a
    // guaranteed dropper; any other key has no source, so forcing it would switch
    // on a per-room search that can never succeed.
    [Fact]
    public void SourceableGateItems_AdmitsDoorKeyOnlyWhenSummonable()
    {
        var reqs = new[] { new RouteRequirement(RouteRequirementKind.DoorKey, new[] { 806 }) };
        Assert.Empty(RouteChoicePlanner.SourceableGateItems(reqs, NoSummons));
        Assert.Equal(new[] { 806 }, RouteChoicePlanner.SourceableGateItems(reqs, AllSummonable));
    }

    // The reported route: the orb gate AND the gate key on one sole route.
    [Fact]
    public void SourceableGateItems_MixedRoute_KeepsOrbAndSummonableKey()
    {
        var reqs = new[]
        {
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 807 }),
            new RouteRequirement(RouteRequirementKind.DoorKey, new[] { 806 }),
        };
        Assert.Equal(
            new[] { 807, 806 },
            RouteChoicePlanner.SourceableGateItems(reqs, id => id == 806));
    }

    [Fact]
    public void SourceableGateItems_DedupesAndDropsNonPositiveIds()
    {
        var reqs = new[]
        {
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 807, 0, 807 }),
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 807 }),
        };
        Assert.Equal(new[] { 807 }, RouteChoicePlanner.SourceableGateItems(reqs, NoSummons));
    }

    [Fact]
    public void SourceableGateItems_NoRequirements_IsEmpty()
        => Assert.Empty(RouteChoicePlanner.SourceableGateItems(
            Array.Empty<RouteRequirement>(), NoSummons));

}
