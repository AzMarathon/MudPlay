namespace MudPlay.Game.Map;

// Damage that comes when a timer a room's spell started runs out: the spell that
// does it, its range as the record gives it, and how many rounds after the cast
// (the durations of the spells whose ending leads to it, added up). Holding breath
// lasts 25 rounds and ends in drowning; drowning lasts 5 and ends in drowned to
// death, so that one comes after 30. The rounds count from the room's first cast:
// its later casts don't start the timer over (user, 2026-10-10).
public readonly record struct RoomSpellDamageStage(int Spell, int MinDamage, int MaxDamage, int AfterRounds);
