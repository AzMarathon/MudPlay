namespace MudPlay.Game.Spells;

// What the buffs being cast on us add to our attacks (BuffCombatCalculator).
// AccuracyMaxSingle is the largest single accuracy grant, for Stock, which takes
// the highest source rather than the sum. Sources lists each spell's part, for the
// attack-row tooltips.
public sealed record BuffCombat(int Accuracy, int AccuracyMaxSingle, int MaxDamage,
    int BsAccuracy, int BsMin, int BsMax, IReadOnlyList<BuffCombatSource> Sources)
{
    public static readonly BuffCombat None = new(0, 0, 0, 0, 0, 0, Array.Empty<BuffCombatSource>());
}

public readonly record struct BuffCombatSource(string Spell, string What, int Value);
