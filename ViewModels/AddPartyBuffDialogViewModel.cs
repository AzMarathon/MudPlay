using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// What the Add-party-buff dialog returns on OK: the picked buff cast-code + its
// recast timer. Null (via a cancelled dialog) means "don't add a slot".
public sealed record AddPartyBuffResult(string Spell, int RecastMarginSec);

// Small picker dialog for adding a party-buff slot: choose a buff from the
// spellbook and set its recast timer, then OK. The Party window then lets you
// pick who it's cast on. Keeps the buff panel compact (no inline typing per row).
public sealed partial class AddPartyBuffDialogViewModel : ObservableObject, IDialogViewModel<AddPartyBuffResult>
{
    public event Action<AddPartyBuffResult?>? CloseRequested;

    public IReadOnlyList<SpellPick> BuffPicks { get; }
    public Func<string?, object?, bool> SpellSuggestionFilter { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private string? _spell;

    [ObservableProperty] private int _recastMarginSec = SpellsSettings.DefaultBlessRecastMarginSec;

    // Enabled once the typed / picked value resolves to a real buff pick, so you
    // can't add an empty or non-buff slot.
    public bool CanAdd =>
        BuffPicks.Any(p => string.Equals(p.Short, (Spell ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase));

    public AddPartyBuffDialogViewModel(
        IReadOnlyList<SpellPick> buffPicks, Func<string?, object?, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(buffPicks);
        ArgumentNullException.ThrowIfNull(filter);
        BuffPicks = buffPicks;
        SpellSuggestionFilter = filter;
    }

    [RelayCommand]
    private void Ok()
    {
        if (!CanAdd) return;
        CloseRequested?.Invoke(new AddPartyBuffResult(Spell!.Trim(), Math.Clamp(RecastMarginSec, 0, 999)));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
