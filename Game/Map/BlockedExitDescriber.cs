using System;

namespace MudPlay.Game.Map;

// Human description of why one exit blocks a walk — shared by the walker's
// blocked-route message and the "run to the blocked room anyway" picker so both
// name the same obstacle. The door cases are the rich ones (which room, which
// direction, the key and the picklocks/strength alternative — all per-direction,
// read from the blocking room's own exit); the other gate kinds get a concise
// phrasing. Requirements are directional: the reason names the room you'd be
// standing in and the way you're heading, so it can't be mistaken for the door's
// far side, which may have entirely different requirements.
public static class BlockedExitDescriber
{
    public static string Describe(
        RoomKey from, Direction dir, in RoomExit exit,
        Func<RoomKey, string?> roomName, Func<int, string?> itemName)
    {
        ArgumentNullException.ThrowIfNull(roomName);
        ArgumentNullException.ThrowIfNull(itemName);

        string where = FormatRoom(from, roomName);
        string way = RoomTooltipBuilder.DirectionLabel(dir);
        return exit.Hint switch
        {
            RoomExitHint.KeyLocked => $"a locked door {way} from {where}{DoorNeeds(in exit, itemName)}",
            RoomExitHint.Door      => $"a door {way} from {where}{DoorNeeds(in exit, itemName)}",
            RoomExitHint.Item or RoomExitHint.Ticket when exit.KeyItemId > 0
                => $"a gate {way} from {where} — needs {ItemText(exit.KeyItemId, itemName)}",
            _ when exit.HasLevelGate
                => $"a level gate {way} from {where} ({RoomExit.FormatLevelGate(exit.MinLevel, exit.MaxLevel)})",
            _ when exit.TollGold > 0 => $"a toll {way} from {where} ({exit.TollGold} gold)",
            _ when exit.HasClassGate => $"a class-restricted exit {way} from {where}",
            _ => $"a blocked exit {way} from {where}",
        };
    }

    // The "needs …" tail for a door: the key and/or the picklocks-or-strength
    // alternative, whichever the exit carries. A key-only door names just the key;
    // a stat-only door names just the skill; a door with both offers the choice.
    private static string DoorNeeds(in RoomExit exit, Func<int, string?> itemName)
    {
        string? key = exit.KeyItemId > 0 ? ItemText(exit.KeyItemId, itemName) : null;
        string? stat = exit.StatRequirement > 0
            ? (exit.CanBash
                ? $"{exit.StatRequirement} picklocks/strength"
                : $"{exit.StatRequirement} picklocks")
            : null;
        if (key is not null && stat is not null) return $" — needs {key}, or {stat}";
        if (key is not null) return $" — needs {key}";
        if (stat is not null) return $" — needs {stat}";
        return " you can't pick or bash";
    }

    private static string ItemText(int id, Func<int, string?> itemName)
        => itemName(id) is { Length: > 0 } name ? $"the {name}" : $"item #{id}";

    private static string FormatRoom(RoomKey key, Func<RoomKey, string?> roomName)
        => roomName(key) is { Length: > 0 } name ? $"{key} ({name})" : key.ToString();
}
