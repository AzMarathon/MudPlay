using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.Settings;

// One self-bless row in the Settings → Spells tab: a 4-letter spell short-code
// picker the CastingDirector recasts when the buff isn't active. Row order is
// cast priority (the bless walk runs top-to-bottom). The active realm sets how
// many rows show — Stock 10, ParaMud 15 — but each row only carries its spell
// code; the sparse map keyed on Index is what persists.
public sealed partial class SelfBlessSlotViewModel : ObservableObject
{
    private readonly Action _onChanged;
    // Returns true when the given cast-code names a spell this class can learn but
    // the character hasn't (cast-on-use item codes never count). Supplied by the
    // parent, which owns the bless suggestion list. Null → never flagged.
    private readonly Func<string?, bool>? _isUnlearned;
    private bool _suppress;

    // 1-based row number, surfaced as the "Bless N" label and used as the
    // persisted sparse-map key.
    public int Index { get; }

    // Display label, e.g. Bless 1.
    public string Label => $"Bless {Index}";

    // Committed 4-letter spell short-code, or blank for an unused slot. Bound
    // to the row's AutoCompleteBox.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Unlearned))]
    private string? _spell;

    // Recast lead in seconds: how far before the buff's tracked expiry the engine
    // recasts it. 0 = wait for actual expiry. Bound to the row's NumericUpDown.
    [ObservableProperty] private int _recastMarginSec = SpellsSettings.DefaultBlessRecastMarginSec;

    // Red-outline flag: the slot names a spell the character hasn't learned. Bound
    // to the row's AutoCompleteBox (Classes.unlearnedSpell). Re-raised on the Spell
    // change above and via RefreshUnlearned when the parent's spellbook changes.
    public bool Unlearned => _isUnlearned?.Invoke(Spell) ?? false;

    public void RefreshUnlearned() => OnPropertyChanged(nameof(Unlearned));

    public SelfBlessSlotViewModel(
        int index, string? spell, int recastMarginSec, Action onChanged,
        Func<string?, bool>? isUnlearned = null)
    {
        Index = index;
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _isUnlearned = isUnlearned;

        _suppress = true;
        Spell = spell;
        RecastMarginSec = recastMarginSec;
        _suppress = false;
    }

    partial void OnSpellChanged(string? value)
    {
        if (_suppress) return;
        _onChanged();
    }

    partial void OnRecastMarginSecChanged(int value)
    {
        if (_suppress) return;
        _onChanged();
    }
}
