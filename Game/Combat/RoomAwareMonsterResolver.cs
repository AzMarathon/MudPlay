using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Resolves a monster display name to the Monsters-table Number of the variant
// actually present in the player's CURRENT room, disambiguating names shared
// across zones (an "orc lieutenant" in the barracks vs one in the slums; a
// "zombie" in the graveyard vs one in the tunnels). A plain name→Number lookup
// picks the first game-data match regardless of zone, so both the HP-lookup and
// the per-monster spell-override features inherit the wrong record.
//
// The candidate set for a room is the same "Also Here" set the nav room tooltip
// shows — lair members + Summoned-By spawns — plus the room's single NPC fixture
// (which the tooltip omits) and the monsters those can summon. Resolution returns
// the Number of the candidate whose display name matches; when the current room
// holds no monster with that name it returns null, so the caller falls back to its
// existing first-match behaviour (never worse than before).
public sealed class RoomAwareMonsterResolver
{
    private readonly GameDataCache _gameData;
    private readonly Func<Room?> _currentRoom;
    private readonly Func<string, string?> _resolveBaseName;
    private readonly MonsterSpawnIndex _spawns;
    private readonly MonsterSummonTargetsIndex _summons;

    public RoomAwareMonsterResolver(
        GameDataCache gameData, Func<Room?> currentRoom,
        Func<string, string?> resolveBaseName,
        MonsterSpawnIndex spawns, MonsterSummonTargetsIndex summons)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _currentRoom = currentRoom ?? throw new ArgumentNullException(nameof(currentRoom));
        _resolveBaseName = resolveBaseName ?? throw new ArgumentNullException(nameof(resolveBaseName));
        _spawns = spawns ?? throw new ArgumentNullException(nameof(spawns));
        _summons = summons ?? throw new ArgumentNullException(nameof(summons));
    }

    // The Number of a monster named `name` placed in / summoned into the player's
    // current room, or null when the room has no such monster (caller falls back).
    public int? ResolveInCurrentRoom(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_currentRoom() is not { } room) return null;
        return ResolveInRoom(room, name);
    }

    // Core resolution against an explicit room — the candidate whose base name
    // matches, or null. Split out so it's testable without a live RoomTracker.
    //
    // The looked-at name carries the game's flavor prefix ("short orc lieutenant",
    // "fierce orc lieutenant") while the Monsters record holds the base name ("orc
    // lieutenant"). ResolveBaseName strips the prefix via the classifier's real
    // per-monster prefix rules (a distinct "short orc lieutenant" record resolves
    // to itself; a prefixed bare record resolves to "orc lieutenant"), then the
    // candidate is matched on that base name. Unresolvable names fall through to a
    // raw exact match, which typically finds nothing → caller's first-match fallback.
    internal int? ResolveInRoom(Room room, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string trimmed = name.Trim();
        string target = _resolveBaseName(trimmed) is { Length: > 0 } b ? b : trimmed;

        foreach (int id in RoomCandidates(room))
        {
            string? candidate = _gameData.FindNameByNumber("Monsters", id);
            if (!string.IsNullOrEmpty(candidate)
                && string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return null;
    }

    private IReadOnlyCollection<int> RoomCandidates(Room room)
    {
        var set = new HashSet<int>();
        // Placed lair members + Summoned-By spawns — the tooltip's "Also Here" set.
        foreach (RoomTooltipBuilder.RoomMonsterRef r in
                 RoomTooltipBuilder.ResolveAlsoHere(room, _gameData, _spawns, out _))
            set.Add(r.Id);
        // The single NPC fixture (bosses / uniques), which the tooltip omits.
        if (room.Npc > 0) set.Add(room.Npc);
        // Widen to the monsters those can summon (a summoner's minions read as being
        // "in the room" without static placement).
        foreach (int id in set.ToArray())
            foreach (int child in _summons.SummonedBy(id))
                set.Add(child);
        return set;
    }
}
