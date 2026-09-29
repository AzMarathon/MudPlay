namespace MudPlay.Game.Combat;

// One damage line as the round ledger credited it: the line, its two sides (room
// display names, DamageLineAttributor.Self for us), and how a dealer the line didn't
// name was settled — our own spell, cast lately (OwnSpell), or the weapon proc of
// whoever just hit that monster (Proc).
public readonly record struct AttributedLine(string Text, DamageAttribution Sides, bool OwnSpell, bool Proc);
