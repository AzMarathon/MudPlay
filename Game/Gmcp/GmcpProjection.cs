using System.Text.Json;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;

namespace MudPlay.Game.Gmcp;

// Turns the server's room and occupant snapshots into the client's own types: a
// Room for the graph and tracker, the RoomObservation a room display would give,
// and the roster's RoomEntity list.
internal static class GmcpProjection
{
    // The server writes a room as "map,room".
    public static RoomKey Key(string wire)
    {
        if (!RoomKey.TryParseWire(wire.Replace(',', '/'), out RoomKey key)) throw new FormatException("Invalid room key.");
        return key;
    }

    // The room as the server shows it, over the graph's own record of it (previous):
    // what the server doesn't carry (trap damage, multi-action steps, casts on the
    // way through) is kept from the graph for an exit that still leads where it did.
    public static Room Room(JsonElement value, Room? previous)
    {
        // Command teleports have no cardinal GMCP slot; retain those graph edges.
        Dictionary<Direction, RoomExit> exits = previous?.Exits
            .Where(e => e.Key > Direction.D)
            .ToDictionary(e => e.Key, e => e.Value) ?? new();
        foreach (JsonElement edge in value.GetProperty("exits").EnumerateArray())
        {
            Direction direction = Enum.Parse<Direction>(edge.GetProperty("dir").GetString()!, ignoreCase: true);
            RoomKey target = Key(edge.GetProperty("to").GetString()!);
            RoomExit prior = previous is not null && previous.Exits.TryGetValue(direction, out RoomExit old) && old.Target == target
                ? old
                : default;
            exits.Add(direction, Exit(edge, target, prior));
        }

        RoomKey id = Key(value.GetProperty("room").GetString()!);
        Room basis = previous ?? new Room { Key = id, Name = "", Exits = exits };
        return basis with
        {
            Name = value.GetProperty("name").GetString()!,
            Light = value.GetProperty("light").GetInt32(),
            Spell = OptionalInt(value, "spell"),
            Exits = exits,
            ExitMask = exits.Keys.Where(d => d <= Direction.D).Aggregate(0u, (mask, d) => mask | (1u << (int)d)),
        };
    }

    private static RoomExit Exit(JsonElement edge, RoomKey target, RoomExit prior)
    {
        JsonElement door = edge.GetProperty("door");
        JsonElement gate = edge.GetProperty("gate");
        bool hasDoor = door.ValueKind == JsonValueKind.Object;
        bool hasGate = gate.ValueKind == JsonValueKind.Object;
        int key = hasDoor ? OptionalInt(door, "key") : 0;
        RoomExitHint hint = edge.GetProperty("kind").GetString() switch
        {
            // The server can't say a hidden exit needs several actions; the graph can.
            "hidden" => prior.Hint == RoomExitHint.MultiActionHidden ? prior.Hint : RoomExitHint.SearchableHidden,
            "text" => RoomExitHint.Text,
            "item" => RoomExitHint.Item,
            "toll" => RoomExitHint.Toll,
            "key" => RoomExitHint.KeyLocked,
            "door" or "gate" => key > 0 ? RoomExitHint.KeyLocked : RoomExitHint.Door,
            _ => RoomExitHint.None,
        };
        string? phrase = edge.GetProperty("phrase").GetString();
        return prior with
        {
            Target = target,
            Hint = hint,
            KeyItemId = key > 0 ? key : hasGate ? OptionalInt(gate, "item") : 0,
            CanBash = hasDoor && door.GetProperty("bashable").GetBoolean(),
            // The toll is in copper; the exit keeps it in gold.
            TollGold = OptionalInt(edge, "toll") / 100,
            FareCopper = 0,
            TextCommands = phrase is null ? null : new[] { phrase },
            MinLevel = hasGate ? OptionalInt(gate, "minLevel") : 0,
            MaxLevel = hasGate ? OptionalInt(gate, "maxLevel") : 0,
            ClassGate = hasGate ? OptionalInt(gate, "class") : 0,
            RaceGate = hasGate ? OptionalInt(gate, "race") : 0,
        };
    }

    // What a room display would have shown: a hidden exit only once found.
    public static RoomObservation Observation(JsonElement value)
    {
        HashSet<Direction> shown = new();
        HashSet<Direction> open = new();
        HashSet<Direction> closed = new();
        foreach (JsonElement edge in value.GetProperty("exits").EnumerateArray())
        {
            Direction direction = Enum.Parse<Direction>(edge.GetProperty("dir").GetString()!, ignoreCase: true);
            if (!edge.GetProperty("hidden").GetBoolean() || edge.GetProperty("found").GetBoolean()) shown.Add(direction);
            JsonElement door = edge.GetProperty("door");
            if (door.ValueKind != JsonValueKind.Object) continue;
            if (door.GetProperty("state").GetString() == "open") open.Add(direction);
            else closed.Add(direction);
        }
        return new(value.GetProperty("name").GetString()!, shown, open, closed);
    }

    // The room's occupants, monsters first. A monster with no static information
    // yet is left out. visibleOnly drops the hidden ones the text doesn't show
    // either (textVisible), for a shadow comparison with the text's roster.
    public static IReadOnlyList<RoomEntity> Entities(GmcpState state, bool visibleOnly = false, IReadOnlySet<string>? textVisible = null)
    {
        List<RoomEntity> result = new();
        if (state.Snapshot("Anomaly.Room.Occupants") is not { } occupants) return result;
        foreach (JsonElement mob in occupants.GetProperty("mobs").EnumerateArray())
        {
            long uid = mob.GetProperty("uid").GetInt64();
            if (!state.StaticMonsters.TryGetValue(uid, out JsonElement info)) continue;
            string display = info.GetProperty("display").GetString()!;
            if (visibleOnly && mob.GetProperty("hidden").GetBoolean() && textVisible?.Contains(display) != true) continue;
            result.Add(new(display, info.GetProperty("name").GetString()!, EntityKind.Monster, info.GetProperty("tpl").GetInt32(), uid));
        }
        foreach (JsonElement player in occupants.GetProperty("players").EnumerateArray())
        {
            string name = player.GetProperty("name").GetString()!;
            if (visibleOnly && player.GetProperty("hidden").GetBoolean() && textVisible?.Contains(name) != true) continue;
            result.Add(new(name, name, EntityKind.Player, null));
        }
        return result;
    }

    private static int OptionalInt(JsonElement value, string name)
    {
        JsonElement field = value.GetProperty(name);
        return field.ValueKind == JsonValueKind.Null ? 0 : field.GetInt32();
    }
}
