using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels;

// One row in Monster Intel's "Apply Debuffs" / "Apply Buffs" pickers — a spell
// the character knows whose effect the matchup what-if can fold in (a debuff onto
// the selected monster, or one of our own offense buffs onto us). Applied checks
// whether it's folded in. Effect is a short human summary of what it does (e.g.
// "-20 AC · slowed", "+7 Stealth"). Key is the stable persistence id (the spell's
// cast code); the VM owns the applied-set state and reacts via PropertyChanged.
public sealed partial class SpellPickRow : ObservableObject
{
    public string Key { get; }
    public string Label { get; }
    public string Effect { get; }

    [ObservableProperty] private bool _applied;

    public SpellPickRow(string key, string label, string effect, bool applied)
    {
        Key = key;
        Label = label;
        Effect = effect;
        _applied = applied;
    }
}
