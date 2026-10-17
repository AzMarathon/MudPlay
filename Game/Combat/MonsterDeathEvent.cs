using System.Collections.Generic;

namespace MudPlay.Game.Combat;

// One monster in a MonsterDeathEvent's RoomSpellRoster, as the room roster named
// it: Number is its Monsters-table number (null when the name didn't resolve to
// one) and Name the resolved name.
public readonly record struct MonsterDeathIdentity(int? Number, string Name);

// Payload of MonsterDeathWatcher.MonsterDied — fired on the exp + *Combat Off*
// sequence, or on the exp line itself for a kill under our own room spell. The
// event names no monster, so consumers attribute the death from their own state,
// typically CombatManager.CurrentTarget. ExperienceGained is the number captured by the most
// recent "You gain N experience." line, or null when no exp line was observed in
// the window. At is the wall-clock time the event fired.
//
// RoomSpellRoster is set for a kill made under our own room-attack spell, raised on
// its exp line (CombatManager.RoomSpellKill): the kinds of monster the room listed
// when it died. One of them is the dead one; which isn't known until the room is
// read again, and the engaged target says nothing, being only the monster the round
// was anchored to. Null for every other death. A consumer that needs a name treats
// such a death as unnamed unless the list holds a single kind.
public readonly record struct MonsterDeathEvent(
    int? ExperienceGained,
    DateTimeOffset At,
    IReadOnlyList<MonsterDeathIdentity>? RoomSpellRoster = null);
