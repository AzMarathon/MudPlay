namespace MudPlay.Game.Map;

// Damage that comes when a timer a room's spell started runs out: the spell that
// does it, its range as the record gives it, and how many rounds after the cast
// (the durations of the spells whose ending leads to it, added up). Holding breath
// lasts 25 rounds and ends in drowning; drowning lasts 5 and ends in drowned to
// death, so that one comes after 30.
public readonly record struct RoomSpellDamageStage(int Spell, int MinDamage, int MaxDamage, int AfterRounds);
