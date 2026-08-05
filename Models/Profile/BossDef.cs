using System;
using System.Collections.Generic;
using System.Linq;

namespace FujinTerm.Models.Profile;

// Whether a boss respawns on a kill-based countdown (Timed) or only on a server
// cleanup (Cleanup — no live timer, shown as "Cleanup").
public enum BossRespawnType { Timed, Cleanup }

// One boss in the boss-timer catalog. Name is the game-data monster spelling — the
// match key for the runtime timer lookup (Monsters.RegenTime, in hours) AND for
// kill detection. Rooms are "map/room" strings; a boss may be placed in several.
// InStock/InParadigm gate visibility per active realm. Timer VALUES are NOT stored
// here — resolved from game data so they stay correct across game versions.
// StopBefore is a user flag: walk-to halts one room short of this boss's rooms.
// ExactSpawn means no early window (100% timer only — e.g. Lord of the Hunt,
// Crimson Mist on Stock). Removed is overlay-only: it hides a seed boss the user
// deleted.
public sealed class BossDef
{
    public string Name { get; set; } = string.Empty;
    public int? MonsterNumber { get; set; }
    public List<string> Rooms { get; set; } = new();
    public bool InStock { get; set; }
    public bool InParadigm { get; set; }
    public BossRespawnType RespawnType { get; set; } = BossRespawnType.Timed;
    public bool ExactSpawn { get; set; }
    public bool StopBefore { get; set; }
    public bool Removed { get; set; }

    // Deep-ish copy so the store can hand out editable rows without mutating the
    // loaded seed/overlay instances.
    public BossDef Clone() => new()
    {
        Name = Name, MonsterNumber = MonsterNumber, Rooms = new List<string>(Rooms),
        InStock = InStock, InParadigm = InParadigm, RespawnType = RespawnType,
        ExactSpawn = ExactSpawn, StopBefore = StopBefore, Removed = Removed,
    };

    // True when this boss carries no user edits relative to a seed entry — the
    // signal BossStore uses to keep the overlay a delta (drop unchanged entries).
    public bool MatchesSeed(BossDef seed) =>
        string.Equals(Name, seed.Name, StringComparison.OrdinalIgnoreCase)
        && MonsterNumber == seed.MonsterNumber
        && InStock == seed.InStock && InParadigm == seed.InParadigm
        && RespawnType == seed.RespawnType && ExactSpawn == seed.ExactSpawn
        && StopBefore == seed.StopBefore && !Removed
        && Rooms.SequenceEqual(seed.Rooms, StringComparer.OrdinalIgnoreCase);
}
