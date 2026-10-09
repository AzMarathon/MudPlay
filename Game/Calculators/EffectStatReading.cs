namespace MudPlay.Game.Calculators;

// What one spell that prints a given effect line does to the six stats
// (STR/INT/WIL/AGL/HEA/CHM). Unknown names the stats it moves by an amount the
// game data can't pin down: a rolled range, or one that grows with the caster's
// level when the caster may be someone else.
public sealed record EffectStatReading(string Spell, int[] Modifiers, StatSet Unknown);
