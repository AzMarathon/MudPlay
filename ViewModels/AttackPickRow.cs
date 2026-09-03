using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels;

// One row in Monster Intel's "Edit Attacks" picker — a single attack the
// character can use (a melee attack type, or an obtained attack spell). The two
// toggles are independent: Shown gates whether the attack appears in the Your
// Matchup panel; IsRoundsAttack is a radio (one across the whole picker) picking
// which attack fills the master list's "Est. Rounds to Kill" column. Key is the
// stable persistence id ("melee:<Type>" / "spell:<Short>"); the VM owns the
// hidden-set + selected-key state and reacts to these via PropertyChanged.
public sealed partial class AttackPickRow : ObservableObject
{
    public string Key { get; }
    public string Label { get; }
    public bool IsSpell { get; }

    [ObservableProperty] private bool _shown;
    [ObservableProperty] private bool _isRoundsAttack;

    public AttackPickRow(string key, string label, bool isSpell, bool shown, bool isRoundsAttack)
    {
        Key = key;
        Label = label;
        IsSpell = isSpell;
        _shown = shown;
        _isRoundsAttack = isRoundsAttack;
    }
}
