using System.Text.Json;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;

namespace MudPlay.Game.Gmcp;

internal static class GmcpProjection
{
    public static RoomKey Key(string wire)
    {
        if (!RoomKey.TryParseWire(wire.Replace(',', '/'), out RoomKey key)) throw new FormatException("Invalid room key.");
        return key;
    }

    public static Room Room(JsonElement value, Room? previous)
    {
        // Command teleports have no cardinal GMCP slot; retain those graph edges.
        Dictionary<Direction, RoomExit> exits = previous?.Exits.Where(e => e.Key > Direction.D).ToDictionary(e => e.Key, e => e.Value) ?? new();
        foreach (JsonElement edge in value.GetProperty("exits").EnumerateArray())
        {
            Direction direction = Enum.Parse<Direction>(edge.GetProperty("dir").GetString()!, true);
            RoomKey target = Key(edge.GetProperty("to").GetString()!);
            RoomExit prior = previous is not null && previous.Exits.TryGetValue(direction, out RoomExit old) && old.Target == target ? old : default;
            JsonElement door = edge.GetProperty("door"), gate = edge.GetProperty("gate");
            int key = door.ValueKind == JsonValueKind.Object ? OptionalInt(door, "key") : 0;
            string kind = edge.GetProperty("kind").GetString()!;
            RoomExitHint hint = kind switch
            {
                "hidden" => prior.Hint == RoomExitHint.MultiActionHidden ? prior.Hint : RoomExitHint.SearchableHidden,
                "text" => RoomExitHint.Text,
                "item" => RoomExitHint.Item,
                "toll" => RoomExitHint.Toll,
                "key" => RoomExitHint.KeyLocked,
                "door" or "gate" => key > 0 ? RoomExitHint.KeyLocked : RoomExitHint.Door,
                _ => RoomExitHint.None
            };
            string? phrase = edge.GetProperty("phrase").GetString();
            int toll = OptionalInt(edge, "toll");
            exits.Add(direction, prior with
            {
                Target = target, Hint = hint,
                KeyItemId = key > 0 ? key : gate.ValueKind == JsonValueKind.Object ? OptionalInt(gate, "item") : 0,
                CanBash = door.ValueKind == JsonValueKind.Object && door.GetProperty("bashable").GetBoolean(),
                TollGold = toll / 100, FareCopper = 0,
                TextCommands = phrase is null ? null : new[] { phrase },
                MinLevel = gate.ValueKind == JsonValueKind.Object ? OptionalInt(gate, "minLevel") : 0,
                MaxLevel = gate.ValueKind == JsonValueKind.Object ? OptionalInt(gate, "maxLevel") : 0,
                ClassGate = gate.ValueKind == JsonValueKind.Object ? OptionalInt(gate, "class") : 0,
                RaceGate = gate.ValueKind == JsonValueKind.Object ? OptionalInt(gate, "race") : 0
            });
        }
        RoomKey id = Key(value.GetProperty("room").GetString()!);
        Room basis = previous ?? new Room { Key = id, Name = "", Exits = exits };
        return basis with { Name = value.GetProperty("name").GetString()!, Light = value.GetProperty("light").GetInt32(),
            Spell = OptionalInt(value, "spell"), Exits = exits, ExitMask = exits.Keys.Where(d => d <= Direction.D).Aggregate(0u, (mask, d) => mask | (1u << (int)d)) };
    }

    public static RoomObservation Observation(JsonElement value)
    {
        HashSet<Direction> shown = new(), open = new(), closed = new();
        foreach (JsonElement edge in value.GetProperty("exits").EnumerateArray())
        {
            Direction direction = Enum.Parse<Direction>(edge.GetProperty("dir").GetString()!, true);
            if (!edge.GetProperty("hidden").GetBoolean() || edge.GetProperty("found").GetBoolean()) shown.Add(direction);
            JsonElement door = edge.GetProperty("door");
            if (door.ValueKind == JsonValueKind.Object)
            {
                if (door.GetProperty("state").GetString() == "open") open.Add(direction);
                else closed.Add(direction);
            }
        }
        return new(value.GetProperty("name").GetString()!, shown, open, closed);
    }

    public static IReadOnlyList<RoomEntity> Entities(GmcpState state, bool visibleOnly = false, IReadOnlySet<string>? textVisible = null)
    {
        List<RoomEntity> result = new();
        if (state.Snapshot("Anomaly.Room.Occupants") is not { } occupants) return result;
        foreach (JsonElement mob in occupants.GetProperty("mobs").EnumerateArray())
        {
            long uid = mob.GetProperty("uid").GetInt64();
            if (!state.StaticMonsters.TryGetValue(uid, out JsonElement info)) continue;
            if (visibleOnly && mob.GetProperty("hidden").GetBoolean() && textVisible?.Contains(info.GetProperty("display").GetString()!) != true) continue;
            result.Add(new(info.GetProperty("display").GetString()!, info.GetProperty("name").GetString()!, EntityKind.Monster, info.GetProperty("tpl").GetInt32(), uid));
        }
        foreach (JsonElement player in occupants.GetProperty("players").EnumerateArray())
        {
            string name = player.GetProperty("name").GetString()!;
            if (visibleOnly && player.GetProperty("hidden").GetBoolean() && textVisible?.Contains(name) != true) continue;
            result.Add(new(name, name, EntityKind.Player, null));
        }
        return result;
    }

    private static int OptionalInt(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? 0 : value.GetProperty(name).GetInt32();
}
