using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One party-member row in the Monster Aggro calculator. It carries the inputs for
// BOTH realm models — the view shows only the fields for the loaded set's realm:
//   Paradigm: Charm, party Position, last-attacker (radio across rows).
//   Stock:    alignment title, provoked (hit it first), incoming hits this beat.
// Result fields are filled in by the parent section's recompute. Any input edit
// notifies the parent to re-run the active model; the row can remove itself.
public sealed partial class AggroMemberRowViewModel : ObservableObject
{
    private readonly Action<AggroMemberRowViewModel> _onChanged;
    private readonly Action<AggroMemberRowViewModel> _onRemove;
    private readonly Action<AggroMemberRowViewModel> _onLastAttacker;
    private bool _suppress;

    // Picker sources — the engines key off these exact strings. "Solo" isn't offered:
    // it's assumed automatically when there's a single member, and the first member of
    // a party is forced to Frontrank (both set by the parent), so the picker only needs
    // the three party ranks.
    public IReadOnlyList<string> PositionOptions { get; } =
        new[] { "Frontrank", "Midrank", "Backrank" };
    public IReadOnlyList<string> AlignmentOptions { get; } =
        new[] { "Saint", "Lawful", "Good", "Neutral", "Seedy", "Outlaw", "Criminal", "Villain", "Fiend" };

    // Display-only row label ("Member N"); shown read-only since the name has no
    // effect on either aggro model.
    [ObservableProperty] private string _name;

    // ---- Paradigm inputs ----
    [ObservableProperty] private int _charm = 50;
    [ObservableProperty] private string _position = "Midrank";
    [ObservableProperty] private bool _isLastAttacker;
    // False for the first member (its rank is forced Solo / Frontrank by the parent),
    // so the view shows a read-only label there instead of the rank picker.
    [ObservableProperty] private bool _positionEditable = true;

    // ---- Stock inputs ----
    [ObservableProperty] private string _alignmentTitle = "Neutral";
    [ObservableProperty] private bool _hasProvoked;
    [ObservableProperty] private int _incomingHits;

    // ---- Paradigm results (parent-filled) ----
    [ObservableProperty] private int _score;
    [ObservableProperty] private string _sharePercentText = "—";
    [ObservableProperty] private string _breakdownText = string.Empty;

    // ---- Stock results (parent-filled) ----
    [ObservableProperty] private bool _isAggroed;
    [ObservableProperty] private string _acquireReason = "—";
    [ObservableProperty] private string _spreadPercentText = "—";

    public AggroMemberRowViewModel(string name,
        Action<AggroMemberRowViewModel> onChanged,
        Action<AggroMemberRowViewModel> onRemove,
        Action<AggroMemberRowViewModel> onLastAttacker)
    {
        _name = name;
        _onChanged = onChanged;
        _onRemove = onRemove;
        _onLastAttacker = onLastAttacker;
    }

    // Flip the last-attacker flag without re-triggering recompute — used by the
    // parent to clear the flag on the other rows (only one member is "last").
    public void SetLastAttackerSilently(bool value)
    {
        _suppress = true;
        IsLastAttacker = value;
        _suppress = false;
    }

    // Set the party position without re-triggering recompute — used by the parent to
    // force the first member's rank (Solo alone / Frontrank in a party); the caller
    // recomputes once after reconciling the whole roster.
    public void SetPositionSilently(string value)
    {
        _suppress = true;
        Position = value;
        _suppress = false;
    }

    partial void OnCharmChanged(int value) => Changed();
    partial void OnPositionChanged(string value) => Changed();
    partial void OnAlignmentTitleChanged(string value) => Changed();
    partial void OnHasProvokedChanged(bool value) => Changed();
    partial void OnIncomingHitsChanged(int value) => Changed();

    partial void OnIsLastAttackerChanged(bool value)
    {
        if (_suppress) return;
        if (value) _onLastAttacker(this);   // radio: parent clears the siblings
        Changed();
    }

    private void Changed()
    {
        if (!_suppress) _onChanged(this);
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);
}
