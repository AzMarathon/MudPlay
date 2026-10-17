using MudPlay.Models.GameData;

namespace MudPlay.Game.Combat;

// What kind of "Also Here" occupant a name resolved to. CombatStateTracker and
// CombatManager only treat Monster entries as candidate targets.
public enum EntityKind
{
    // Name matched a PlayerRecord in the active per-BBS player database
    // (case-insensitive on PlayerRecord.GivenName).
    Player,

    // Name matched a monster — a MonsterMessageRecord.Name or a Monsters-table
    // row — either directly or after stripping a leading word in the per-set
    // flavor vocabulary (Services.FlavorPrefixStore).
    Monster,

    // Name matched neither — RoomEntityClassifier emits a Warn-level LogPane row
    // carrying the raw "Also Here:" line as Context so the user can double-click
    // to open the fix dialog and resolve it as a flavor-prefix addition or a
    // player observation.
    Unknown,
}

// One classified occupant from an "Also here:" line.
//
// RawName is the name verbatim as it appeared on the wire after splitting +
// trimming trailing punctuation / parenthetical suffix (e.g. "nasty giant rat",
// "Raijin" from "Raijin (sneaking)"). ResolvedName is the canonical name for
// downstream lookups: monster BaseName when Kind is Monster,
// PlayerRecord.GivenName when Player, equal to RawName for Unknown. CombatManager
// sends RawName so the server engages that instance, and keys its per-species
// state on ResolvedName. Kind is Player / Monster / Unknown. MonsterNumber is
// MonsterRecord.Number when Kind is Monster, else null; CombatManager resolves
// the monster's overlay (relationship, priority) by it.
public readonly record struct RoomEntity(
    string RawName,
    string ResolvedName,
    EntityKind Kind,
    int? MonsterNumber);
