using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// What the Add-buff dialog returns on OK: the picked buff cast-code, its recast
// timer, and the per-slot conditions. Null (via a cancelled dialog) means "don't
// add / change the slot".
public sealed record AddBuffResult(
    string Spell,
    int RecastMarginSec,
    bool OnlyWhenHpFull,
    bool OnlyWhenMaFull,
    bool OnlyWhenDark,
    bool CastBeforeRestingForMana,
    int RerollCount,
    int? RerollThreshold);

// Picker dialog for adding / editing a buff slot: choose a buff, set its recast
// timer, and pick the per-slot conditions. The condition rows adapt to the spell —
// a light spell offers "only when dark", a mana-regen roll spell offers the reroll
// config (threshold + max rerolls) and a "cast before resting" toggle. Targeting
// (self / members) is then chosen in the row back in the Buff Watchdog.
public sealed partial class AddBuffDialogViewModel : ObservableObject, IDialogViewModel<AddBuffResult>
{
    public event Action<AddBuffResult?>? CloseRequested;

    public IReadOnlyList<SpellPick> BuffPicks { get; }
    public Func<string?, object?, bool> SpellSuggestionFilter { get; }
    private readonly Func<string?, bool> _isLightSpell;
    private readonly Func<string?, bool> _isRollSpell;
    private readonly bool _isStockRealm;
    private readonly Func<string?, (int Worst, int Best)?>? _tickRange;

    // Cached worst/best mana tick for the picked roll spell (Stock only), refreshed
    // when the spell changes so the slider bounds follow the pick.
    private (int Worst, int Best)? _range;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyPropertyChangedFor(nameof(IsLightSpell))]
    [NotifyPropertyChangedFor(nameof(IsRollSpell))]
    [NotifyPropertyChangedFor(nameof(ShowRerollSlider))]
    [NotifyPropertyChangedFor(nameof(ShowRerollNumeric))]
    [NotifyPropertyChangedFor(nameof(TickWorst))]
    [NotifyPropertyChangedFor(nameof(TickBest))]
    [NotifyPropertyChangedFor(nameof(RerollBoundsText))]
    [NotifyPropertyChangedFor(nameof(RerollThresholdSlider))]
    private string? _spell;

    partial void OnSpellChanged(string? value)
        => _range = _isStockRealm ? _tickRange?.Invoke(value) : null;

    [ObservableProperty] private int _recastMarginSec = SpellsSettings.DefaultBlessRecastMarginSec;

    // Per-slot conditions.
    [ObservableProperty] private bool _onlyWhenHpFull;
    [ObservableProperty] private bool _onlyWhenMaFull;
    [ObservableProperty] private bool _onlyWhenDark;
    [ObservableProperty] private bool _castBeforeRestingForMana;
    [ObservableProperty] private int _rerollCount;
    [ObservableProperty] private int? _rerollThreshold;

    // Whether the picked spell is a light spell (offers "only when dark") or a
    // mana-regen roll spell (offers the reroll config + "cast before resting").
    public bool IsLightSpell => _isLightSpell(Spell);
    public bool IsRollSpell => _isRollSpell(Spell);

    // Reroll wording adapts to the realm: Stock judges the roll from the observed
    // passive mana TICK (an MP jump on the statline); Paradigm from the rolled
    // percent read off `abil 145`.
    public string RerollThresholdLabel => _isStockRealm ? "Reroll below tick" : "Reroll below abil 145";
    public string RerollThresholdTip => _isStockRealm
        ? "Reroll while the observed passive mana tick lands below this MP. Blank = don't reroll."
        : "Reroll while the spell's rolled mana-regen value — its `abil 145` spells contribution, which can be negative — lands below this. Blank = don't reroll.";

    // Stock reroll threshold slider: when the roll spell's live worst/best tick is
    // known, show a slider between them so the threshold reads against min↔max;
    // otherwise fall back to the plain numeric field.
    public bool ShowRerollSlider => IsRollSpell && _range is not null;
    public bool ShowRerollNumeric => IsRollSpell && !ShowRerollSlider;
    public double TickWorst => _range?.Worst ?? 0;
    // Keep Max strictly above Min so the Slider always has a usable range.
    public double TickBest => _range is { } r && r.Best > r.Worst ? r.Best : TickWorst + 1;
    public string RerollBoundsText =>
        _range is { } r ? $"worst {r.Worst} … best {r.Best} MP per tick" : string.Empty;

    // The slider's value, mapped onto the stored threshold (defaults to the worst
    // tick — i.e. accept anything — until the user drags it up).
    public double RerollThresholdSlider
    {
        get => RerollThreshold ?? (int)TickWorst;
        set => RerollThreshold = (int)System.Math.Round(value);
    }

    partial void OnRerollThresholdChanged(int? value) => OnPropertyChanged(nameof(RerollThresholdSlider));

    // Enabled once the typed / picked value resolves to a real buff pick, so you
    // can't add an empty or non-buff slot.
    public bool CanAdd =>
        BuffPicks.Any(p => string.Equals(p.Short, (Spell ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase));

    // Whether this dialog is editing an existing slot (vs adding a new one) —
    // drives the title + OK-button label.
    public bool IsEditing { get; }
    public string DialogTitle => IsEditing ? "Edit buff" : "Add buff";
    public string OkLabel => IsEditing ? "Save" : "OK";

    public AddBuffDialogViewModel(
        IReadOnlyList<SpellPick> buffPicks, Func<string?, object?, bool> filter,
        Func<string?, bool> isLightSpell, Func<string?, bool> isRollSpell,
        bool isStockRealm = false, Func<string?, (int Worst, int Best)?>? tickRange = null,
        AddBuffResult? initial = null)
    {
        ArgumentNullException.ThrowIfNull(buffPicks);
        ArgumentNullException.ThrowIfNull(filter);
        BuffPicks = buffPicks;
        SpellSuggestionFilter = filter;
        _isLightSpell = isLightSpell;
        _isRollSpell = isRollSpell;
        _isStockRealm = isStockRealm;
        _tickRange = tickRange;
        IsEditing = initial is not null;
        if (initial is { } i)
        {
            _spell = i.Spell;
            _recastMarginSec = i.RecastMarginSec;
            _onlyWhenHpFull = i.OnlyWhenHpFull;
            _onlyWhenMaFull = i.OnlyWhenMaFull;
            _onlyWhenDark = i.OnlyWhenDark;
            _castBeforeRestingForMana = i.CastBeforeRestingForMana;
            _rerollCount = i.RerollCount;
            _rerollThreshold = i.RerollThreshold;
            _range = _isStockRealm ? _tickRange?.Invoke(_spell) : null;
        }
    }

    [RelayCommand]
    private void Ok()
    {
        if (!CanAdd) return;
        CloseRequested?.Invoke(new AddBuffResult(
            Spell!.Trim(),
            Math.Clamp(RecastMarginSec, 0, 999),
            OnlyWhenHpFull,
            OnlyWhenMaFull,
            IsLightSpell && OnlyWhenDark,
            IsRollSpell && CastBeforeRestingForMana,
            IsRollSpell ? Math.Clamp(RerollCount, 0, 20) : 0,
            IsRollSpell ? RerollThreshold : null));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
