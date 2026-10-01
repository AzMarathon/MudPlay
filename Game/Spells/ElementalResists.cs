namespace MudPlay.Game.Spells;

// A character's resist percentages against each spell damage type, summed over
// every source that grants them (GAME_MECHANICS "Elemental resistance — flat,
// deterministic, pre-emptable"). Poison is the ImmuPoison value, which Stock also
// applies as a damage cut.
public readonly record struct ElementalResists(
    int Cold, int Fire, int Stone, int Lightning, int Water, int Poison)
{
    public int For(SpellDamageElement element) => element switch
    {
        SpellDamageElement.Cold => Cold,
        SpellDamageElement.Fire => Fire,
        SpellDamageElement.Stone => Stone,
        SpellDamageElement.Lightning => Lightning,
        SpellDamageElement.Water => Water,
        SpellDamageElement.Poison => Poison,
        _ => 0,
    };

    public static ElementalResists operator +(ElementalResists a, ElementalResists b) => new(
        a.Cold + b.Cold, a.Fire + b.Fire, a.Stone + b.Stone,
        a.Lightning + b.Lightning, a.Water + b.Water, a.Poison + b.Poison);
}
