using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels;

// One row in Monster Intel's "Apply Debuffs" picker — a single stat-affecting
// debuff spell the character knows (a 0-energy enemy-target spell that lowers a
// monster's AC / DR / Dodge / accuracy or slows it). Applied checks whether its
// effect is folded onto the selected monster in the matchup what-if. Effect is a
// short human summary of what it strips (e.g. "-20 AC · slowed"). Key is the
// stable persistence id (the spell's cast code); the VM owns the applied-set
// state and reacts via PropertyChanged.
public sealed partial class DebuffPickRow : ObservableObject
{
    public string Key { get; }
    public string Label { get; }
    public string Effect { get; }

    [ObservableProperty] private bool _applied;

    public DebuffPickRow(string key, string label, string effect, bool applied)
    {
        Key = key;
        Label = label;
        Effect = effect;
        _applied = applied;
    }
}
