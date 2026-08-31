namespace MudPlay.Game.Spells;

// The defensive contribution of the character's configured, self-applicable
// buffs, assuming they're up — see BuffDefenseCalculator.
//   Ac          — summed flat AC (abil 2 + AC-Blur 10).
//   Dr          — summed damage resist (abil 7), already ÷10 to the applied value.
//   ProtEvil    — summed Prot-Evil ward (abil 24) — conditional (vs evil only).
//   HasShadow   — any buff grants the Shadow property (abil 9), a flat +10 AC.
//   HasVileWard — any buff grants a vile ward (abil 1113), scaling with evil.
public readonly record struct BuffDefense(
    int Ac, double Dr, int ProtEvil, bool HasShadow, bool HasVileWard)
{
    public bool Any => Ac != 0 || Dr != 0 || ProtEvil != 0 || HasShadow || HasVileWard;
}
