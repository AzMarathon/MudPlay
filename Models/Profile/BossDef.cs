using System;
using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Models.Profile;

// Whether a boss respawns on a kill-based countdown (Timed) or only on a server
// cleanup (Cleanup — no live timer, shown as "Cleanup").
public enum BossRespawnType { Timed, Cleanup }

// One boss in the boss-timer catalog. Name is what the list knows the boss by: its
// key there, in the kill-times and in events. It is usually the game-data monster
// spelling too, which the timer lookup (Monsters.RegenTime, in hours) and kill
// detection match on; GameName says otherwise. Rooms are "map/room" strings; a boss
// may be placed in several.
// InStock/InParadigm gate visibility per active realm. Timer VALUES are NOT stored
// here — resolved from game data so they stay correct across game versions.
// StopBefore is a user flag: walk-to halts one room short of this boss's rooms. On
// by default, so a seed boss or an entry stored without the flag stops short.
// Removed is overlay-only: it hides a seed boss the user deleted.
// RespawnHoursOverride is a user fallback for bosses game data can't resolve a
// timer for — null means "use game data" (the normal case); a value forces that
// respawn length regardless of the loaded set. Notes is free-text amplifying info
// for bosses with nuances.
public sealed class BossDef
{
    public string Name { get; set; } = string.Empty;
    // What the game calls it, when Name can't be that. The game has bosses that share
    // a name (two Nahrs, two master assassins, each with its own room and timer), and
    // the list holds one boss per Name, so each of those gets a Name that tells it
    // apart and keeps the shared one here. The same goes for a box whose item name
    // ("wooden box") says nothing about where it is. Empty means Name is the game's.
    public string GameName { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public string MatchName => string.IsNullOrWhiteSpace(GameName) ? Name : GameName;
    // The Monsters record this boss is. Several records can share a name with
    // different timers, so the timer is read from this one (BossCatalog).
    public int? MonsterNumber { get; set; }
    public List<string> Rooms { get; set; } = new();
    public bool InStock { get; set; }
    public bool InParadigm { get; set; }
    public BossRespawnType RespawnType { get; set; } = BossRespawnType.Timed;
    public bool StopBefore { get; set; } = true;
    // When set, the moment this boss dies the client blindly fires a `get <item>`
    // for every item in its game-data drop table — no room re-parse. Default off.
    public bool GrabAll { get; set; }
    // What Stop before and Grab All go back to on the Bosses tab's "Reset to default".
    // Set per boss in the Manage dialog, so a list someone edits or shares carries
    // its own defaults. Null is "not stated": an overlay entry saved before these
    // existed takes the seed boss's value (BossStore.Resolve), and failing that
    // Stop before resets to on and Grab All to off.
    public bool? DefaultStopBefore { get; set; }
    public bool? DefaultGrabAll { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool ResetStopBefore => DefaultStopBefore ?? true;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ResetGrabAll => DefaultGrabAll ?? false;
    public int? RespawnHoursOverride { get; set; }
    public string Notes { get; set; } = string.Empty;
    // Whether this boss appears in the Player Workshop Bosses table. Default true;
    // unchecking it in the Manage dialog hides the row from the tab (the boss is
    // still tracked — timers, @timer, kill detection — just not listed there).
    public bool ShowInTable { get; set; } = true;
    public bool Removed { get; set; }

    // Deep-ish copy so the store can hand out editable rows without mutating the
    // loaded seed/overlay instances.
    public BossDef Clone() => new()
    {
        Name = Name, GameName = GameName, MonsterNumber = MonsterNumber, Rooms = new List<string>(Rooms),
        InStock = InStock, InParadigm = InParadigm, RespawnType = RespawnType,
        StopBefore = StopBefore, GrabAll = GrabAll,
        DefaultStopBefore = DefaultStopBefore, DefaultGrabAll = DefaultGrabAll,
        RespawnHoursOverride = RespawnHoursOverride,
        Notes = Notes, ShowInTable = ShowInTable, Removed = Removed,
    };

    // True when this boss carries no user edits relative to a seed entry — the
    // signal BossStore uses to keep the overlay a delta (drop unchanged entries).
    public bool MatchesSeed(BossDef seed) =>
        string.Equals(Name, seed.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(MatchName, seed.MatchName, StringComparison.OrdinalIgnoreCase)
        && MonsterNumber == seed.MonsterNumber
        && InStock == seed.InStock && InParadigm == seed.InParadigm
        && RespawnType == seed.RespawnType
        && StopBefore == seed.StopBefore && GrabAll == seed.GrabAll
        && ResetStopBefore == seed.ResetStopBefore && ResetGrabAll == seed.ResetGrabAll
        && RespawnHoursOverride == seed.RespawnHoursOverride
        && string.Equals(Notes, seed.Notes, StringComparison.Ordinal)
        && ShowInTable == seed.ShowInTable && !Removed
        && Rooms.SequenceEqual(seed.Rooms, StringComparer.OrdinalIgnoreCase);
}
