using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Spells;

namespace MudPlay.ViewModels.GameData.Edit;

// Interactive damage read-out for a damage spell's Game Data tab: a Level picker
// (starting at the spell's learned level, ticking up to its cap) plus, where they
// bear on the spell, Magic-Resist and elemental-resist pickers — the min/max
// damage recomputes live as any of them changes. Replaces the old static, and
// contradictory, "Dmg …" summary + "Damage(-MR): …" growth rows. Pure display —
// the math lives in SpellDamageCalculator.
public sealed partial class SpellDamageCalcViewModel : ObservableObject
{
    private readonly SpellFormulaInput _formula;

    public SpellDamageCalcViewModel(in SpellFormulaInput formula)
    {
        _formula = formula;
        MinLevel = System.Math.Max(1, formula.ReqLevel);
        MaxLevel = formula.Cap > 0 ? System.Math.Max(formula.Cap, MinLevel) : System.Math.Max(MinLevel, 100);
        _level = MinLevel;

        ShowMagicResist = SpellDamageCalculator.UsesMagicResist(formula);
        SpellDamageElement element = SpellDamageCalculator.Element(formula);
        ShowElementalResist = element is not (SpellDamageElement.None or SpellDamageElement.Poison);
        ElementalResistLabel = element switch
        {
            SpellDamageElement.Cold => "Cold resist %",
            SpellDamageElement.Fire => "Fire resist %",
            SpellDamageElement.Stone => "Stone resist %",
            SpellDamageElement.Lightning => "Lightning resist %",
            SpellDamageElement.Water => "Water resist %",
            _ => "Elemental resist %",
        };

        GrowthText = $"grows  min {PerLevel(formula.MinInc, formula.MinIncLVLs)} · "
            + $"max {PerLevel(formula.MaxInc, formula.MaxIncLVLs)}  (to level {MaxLevel})";

        Recompute();
    }

    // Selected cast level; recompute the damage on change. Clamped by the picker's
    // Minimum/Maximum (ReqLevel..cap).
    [ObservableProperty] private int _level;

    // Target's magic resist, when the spell is MR-affected (code 17 Damage(-MR)).
    [ObservableProperty] private int _magicResist;

    // Target's elemental resist %, when the spell is elemental.
    [ObservableProperty] private int _elementalResist;

    // The live min/max damage a single cast lands ("24 to 42", or a single value).
    [ObservableProperty] private string _damageText = string.Empty;

    public int MinLevel { get; }
    public int MaxLevel { get; }
    public bool ShowMagicResist { get; }
    public bool ShowElementalResist { get; }
    public string ElementalResistLabel { get; }

    // How min and max grow per level — the scaling the old "Level Scaling" row
    // showed, restated for this calculator.
    public string GrowthText { get; }

    partial void OnLevelChanged(int value) => Recompute();
    partial void OnMagicResistChanged(int value) => Recompute();
    partial void OnElementalResistChanged(int value) => Recompute();

    private void Recompute()
    {
        (long lo, long hi) = SpellDamageCalculator.Compute(_formula, Level, MagicResist, ElementalResist);
        DamageText = lo == hi
            ? lo.ToString(CultureInfo.InvariantCulture)
            : $"{lo.ToString(CultureInfo.InvariantCulture)} to {hi.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string PerLevel(int inc, int lvls)
        => lvls > 0 && inc != 0
            ? $"+{inc.ToString(CultureInfo.InvariantCulture)}/{lvls.ToString(CultureInfo.InvariantCulture)}lv"
            : "flat";
}
