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
    int? RerollThreshold,
    bool RerollInfinite = false,
    // Draw items: the outcomes (spell numbers) the user unticked.
    IReadOnlyList<int>? RejectedOutcomes = null,
    // The buff's own mana floor and whether it casts on us in a rest or a fight.
    int BlessIfAboveMa = BuffSlot.DefaultBlessIfAboveMa,
    bool BlessWhileResting = false,
    bool BlessDuringCombat = false);

// One entry in the Add-buff dropdown: the cast Code the game accepts, a Display
// showing the buff's name + the level it's learned at ("bless (Lvl 2)"), and
// whether it's selectable — Enabled is false for a buff already held by another
// slot, so it shows greyed rather than vanishing (you can see it's taken).
public sealed record BuffPickOption(string Code, string Display, bool Enabled);

// Picker dialog for adding / editing a buff slot: choose a buff, set its recast
// timer, and pick the per-slot conditions. The condition rows adapt to the spell —
// a light spell offers "only when dark", a mana-regen roll spell offers the reroll
// config (threshold + max rerolls) and a "cast before resting" toggle. Targeting
// (self / members) is then chosen in the row back in the Buff Watchdog.
public sealed partial class AddBuffDialogViewModel : ObservableObject, IDialogViewModel<AddBuffResult>
{
    public event Action<AddBuffResult?>? CloseRequested;

    // Every learned buff the character could slot, as dropdown options — a buff
    // already held by another slot is present but disabled (Enabled = false) so
    // it reads as taken rather than silently missing.
    public IReadOnlyList<BuffPickOption> PickOptions { get; }
    private readonly Func<string?, bool> _isLightSpell;
    private readonly Func<string?, bool> _isRollSpell;
    private readonly bool _isStockRealm;
    private readonly Func<string?, string?>? _tickSteps;
    private readonly Func<string?, (int Min, int Max)?>? _rollRange;
    private readonly Func<string?, IReadOnlyList<CastOutcome>>? _outcomesOf;
    private readonly Func<string?, bool>? _isNoRedrawDraw;

    // A draw item that can't be used again while its card is up (Stock's deck): no
    // tick boxes, since no card can be turned down. The dialog says so instead.
    public bool IsNoRedrawDraw => _isNoRedrawDraw?.Invoke(Spell) == true;

    // Its recast can't lead the wear-off either: a use before the card is gone is
    // refused. 0 (at wear-off) or negative (some time after) only (user, 2026-10-02).
    public int RecastMarginMaximum => IsNoRedrawDraw ? 0 : 999;
    private readonly HashSet<int> _initiallyRejected = new();

    // A draw item (a deck of cards) deals one of several buffs at random. One tick
    // box per buff: a ticked one is kept when drawn, an unticked one is drawn again.
    public System.Collections.ObjectModel.ObservableCollection<BuffOutcomeToggle> Outcomes { get; } = new();
    public bool HasOutcomes => Outcomes.Count > 0;
    public bool NoOutcomeTicked => HasOutcomes && !Outcomes.Any(static o => o.IsChecked);

    private void RebuildOutcomes()
    {
        Outcomes.Clear();
        foreach (CastOutcome o in _outcomesOf?.Invoke(Spell) ?? Array.Empty<CastOutcome>())
            Outcomes.Add(new BuffOutcomeToggle(
                o.SpellNumber,
                o.ChancePercent > 0 ? $"{o.Name} ({o.ChancePercent}%)" : o.Name,
                OutcomeTip(o),
                !_initiallyRejected.Contains(o.SpellNumber),
                OnOutcomeToggled));
        OnPropertyChanged(nameof(HasOutcomes));
        OnPropertyChanged(nameof(IsNoRedrawDraw));
        OnPropertyChanged(nameof(RecastMarginMaximum));
        if (RecastMarginSec > RecastMarginMaximum) RecastMarginSec = RecastMarginMaximum;
        OnOutcomeToggled();
    }

    private static string OutcomeTip(CastOutcome o)
    {
        string effect = string.IsNullOrWhiteSpace(o.Effect) ? "No effect figures in the game data." : o.Effect;
        if (o.DurationRounds <= 0) return effect;
        double minutes = o.DurationRounds * SpellCalculator.SpellRoundSecondsWallClock / 60.0;
        return $"{effect}\nLasts about {minutes:0.#} min.";
    }

    private void OnOutcomeToggled()
    {
        OnPropertyChanged(nameof(NoOutcomeTicked));
        OnPropertyChanged(nameof(CanAdd));
    }

    partial void OnSpellChanged(string? value) => RebuildOutcomes();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyPropertyChangedFor(nameof(IsLightSpell))]
    [NotifyPropertyChangedFor(nameof(IsRollSpell))]
    [NotifyPropertyChangedFor(nameof(ShowRerollNumeric))]
    [NotifyPropertyChangedFor(nameof(RerollNumericMinimum))]
    [NotifyPropertyChangedFor(nameof(RerollNumericMaximum))]
    [NotifyPropertyChangedFor(nameof(RerollRollRangeText))]
    [NotifyPropertyChangedFor(nameof(RerollTickStepsText))]
    private string? _spell;

    // The dropdown's selected option ↔ the stored cast code. Picking one drives
    // Spell (which cascades the light / roll-spell detection); pre-set from
    // `initial` when editing an existing slot. A disabled (already-slotted) option
    // can't be selected, so Spell only ever lands on an addable buff.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private BuffPickOption? _selectedPick;

    partial void OnSelectedPickChanged(BuffPickOption? value) => Spell = value?.Code;

    [ObservableProperty] private int _recastMarginSec = SpellsSettings.DefaultBlessRecastMarginSec;

    // Per-slot conditions.
    [ObservableProperty] private int _blessIfAboveMa = BuffSlot.DefaultBlessIfAboveMa;
    [ObservableProperty] private bool _blessWhileResting;
    [ObservableProperty] private bool _blessDuringCombat;

    // The mana floor is a percent of max mana, or a raw amount when Settings → Health
    // reads its mana thresholds as absolute values.
    private readonly bool _manaFloorIsAbsolute;
    public string BlessIfAboveLabel => _manaFloorIsAbsolute ? "Cast if mana ≥" : "Cast if mana ≥ %";
    public int BlessIfAboveMaximum => _manaFloorIsAbsolute ? 100_000 : 100;

    [ObservableProperty] private bool _onlyWhenHpFull;
    [ObservableProperty] private bool _onlyWhenMaFull;
    [ObservableProperty] private bool _onlyWhenDark;
    [ObservableProperty] private bool _castBeforeRestingForMana;
    [ObservableProperty] private int _rerollCount;
    [ObservableProperty] private int? _rerollThreshold;

    // "Reroll infinite" — keep re-casting until the roll clears the threshold, so the
    // user needn't set an obscene Max-rerolls. When on, the Max-rerolls picker is inert.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RerollCountEnabled))]
    private bool _rerollInfinite;

    // The Max-rerolls picker is live only when rerolling is capped (infinite off).
    public bool RerollCountEnabled => !RerollInfinite;

    // Whether the picked spell is a light spell (offers "only when dark") or a
    // mana-regen roll spell (offers the reroll config + "cast before resting").
    public bool IsLightSpell => _isLightSpell(Spell);
    public bool IsRollSpell => _isRollSpell(Spell);

    // Both realms judge the rolled mana-regen percent: Paradigm reads it off
    // `abil 145`, Stock reads it back off the natural mana tick it pays.
    public string RerollThresholdLabel => _isStockRealm ? "Reroll below roll" : "Reroll below abil 145";
    public string RerollThresholdTip => _isStockRealm
        ? "Reroll while the spell's rolled mana-regen value lands below this. Stock has no `abil`, so the roll is read back off your next natural mana tick; the tick is whole MP, so the roll is known to within a band, and a band that reaches this counts as good. Blank = don't reroll."
        : "Reroll while the spell's rolled mana-regen value — its `abil 145` spells contribution, which can be negative — lands below this. Blank = don't reroll.";

    // The threshold box spans exactly what the spell can roll at the character's level,
    // negatives included (a flux roll can land well below zero); ±999 when unknown.
    public bool ShowRerollNumeric => IsRollSpell;
    private (int Min, int Max)? RollRange => _rollRange?.Invoke(Spell);
    public decimal RerollNumericMinimum => RollRange?.Min ?? -999;
    public decimal RerollNumericMaximum => RollRange?.Max ?? 999;
    public string RerollRollRangeText =>
        RollRange is { } r ? $"rolls {r.Min} … {r.Max} at your level" : string.Empty;

    // Stock: the roll each natural-tick amount needs. The tick is truncated to whole MP,
    // so only these step values change what you're paid — worth setting the threshold on.
    public string RerollTickStepsText => _isStockRealm ? _tickSteps?.Invoke(Spell) ?? string.Empty : string.Empty;

    // Enabled once a selectable (not already-slotted) buff is picked, so you can't
    // add an empty slot or one that would duplicate an existing buff.
    public bool CanAdd => SelectedPick is { Enabled: true } && !NoOutcomeTicked;

    // Whether this dialog is editing an existing slot (vs adding a new one) —
    // drives the title + OK-button label.
    public bool IsEditing { get; }
    public string DialogTitle => IsEditing ? "Edit buff" : "Add buff";
    public string OkLabel => IsEditing ? "Save" : "OK";

    public AddBuffDialogViewModel(
        IReadOnlyList<BuffPickOption> pickOptions,
        Func<string?, bool> isLightSpell, Func<string?, bool> isRollSpell,
        bool isStockRealm = false, Func<string?, string?>? tickSteps = null,
        AddBuffResult? initial = null, Func<string?, (int Min, int Max)?>? rollRange = null,
        Func<string?, IReadOnlyList<CastOutcome>>? outcomesOf = null,
        Func<string?, bool>? isNoRedrawDraw = null,
        bool manaFloorIsAbsolute = false)
    {
        _manaFloorIsAbsolute = manaFloorIsAbsolute;
        _outcomesOf = outcomesOf;
        _isNoRedrawDraw = isNoRedrawDraw;
        foreach (int rejected in initial?.RejectedOutcomes ?? Array.Empty<int>()) _initiallyRejected.Add(rejected);
        ArgumentNullException.ThrowIfNull(pickOptions);
        PickOptions = pickOptions;
        _isLightSpell = isLightSpell;
        _isRollSpell = isRollSpell;
        _isStockRealm = isStockRealm;
        _tickSteps = tickSteps;
        _rollRange = rollRange;
        IsEditing = initial is not null;
        if (initial is { } i)
        {
            _spell = i.Spell;
            _selectedPick = pickOptions.FirstOrDefault(
                o => string.Equals(o.Code, i.Spell, StringComparison.OrdinalIgnoreCase));
            _recastMarginSec = i.RecastMarginSec;
            _blessIfAboveMa = i.BlessIfAboveMa;
            _blessWhileResting = i.BlessWhileResting;
            _blessDuringCombat = i.BlessDuringCombat;
            _onlyWhenHpFull = i.OnlyWhenHpFull;
            _onlyWhenMaFull = i.OnlyWhenMaFull;
            _onlyWhenDark = i.OnlyWhenDark;
            _castBeforeRestingForMana = i.CastBeforeRestingForMana;
            _rerollCount = i.RerollCount;
            _rerollThreshold = i.RerollThreshold;
            _rerollInfinite = i.RerollInfinite;
            RebuildOutcomes();
        }
    }

    [RelayCommand]
    private void Ok()
    {
        if (!CanAdd) return;
        CloseRequested?.Invoke(new AddBuffResult(
            Spell!.Trim(),
            // Negative = recast AFTER wear-off (lapse |margin| seconds first), to spread
            // out mana use; positive = recast that many seconds before expiry.
            Math.Clamp(RecastMarginSec, -999, RecastMarginMaximum),
            OnlyWhenHpFull,
            OnlyWhenMaFull,
            IsLightSpell && OnlyWhenDark,
            IsRollSpell && CastBeforeRestingForMana,
            IsRollSpell ? Math.Clamp(RerollCount, 0, 20) : 0,
            IsRollSpell ? RerollThreshold : null,
            IsRollSpell && RerollInfinite,
            Outcomes.Where(static o => !o.IsChecked).Select(static o => o.SpellNumber).ToList(),
            Math.Clamp(BlessIfAboveMa, 0, BlessIfAboveMaximum),
            BlessWhileResting,
            BlessDuringCombat));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
