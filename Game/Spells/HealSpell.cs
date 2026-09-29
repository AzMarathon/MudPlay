namespace MudPlay.Game.Spells;

// One instant heal spell as HealLineReader sees it: its Spells-table number and name,
// its Targets value (13 = the caster's whole party), and the level-scaled formula
// that gives its heal range when a line doesn't print the amount.
public sealed record HealSpell(int Number, string Name, int Targets, SpellFormulaInput Formula);
