namespace MudPlay.Game.Combat;

// One damage line as the round ledger credited it: the line, its two sides (room
// display names, DamageLineAttributor.Self for us), and how a dealer the line didn't
// name was settled — our own spell, cast lately (OwnSpell), or the weapon proc of
// whoever just hit that monster (Proc). Foes > 0 marks our room spell, which named no
// victim: each of the room's Foes monsters took Sides.Amount.
public readonly record struct AttributedLine(string Text, DamageAttribution Sides, bool OwnSpell, bool Proc, int Foes = 0);
