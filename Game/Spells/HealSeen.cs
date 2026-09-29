namespace MudPlay.Game.Spells;

// A heal HealLineReader recognised on a line. Target is who was healed as the line
// names them, or null for a party heal (the caster's whole party). Amount is the
// line's own number when AmountShown, otherwise the spell's average heal at
// CasterLevel. Caster is the casting player when the line names one; Ours marks our
// own cast. ViewedAsMember marks a party heal we saw from inside the party it landed
// on (the line we get when it heals us), so it was our party whoever cast it.
public readonly record struct HealSeen(
    string? Target,
    int Amount,
    bool AmountShown,
    int SpellNumber,
    string SpellName,
    string? Caster,
    bool Ours,
    bool ViewedAsMember,
    int CasterLevel);
