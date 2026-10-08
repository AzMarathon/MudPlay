using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// The Item Finder's Configure Estimates window: who is swinging and at what, for the
// Gear Finder's trial damage readout. The character side is the stats with nothing
// worn (the trial set's bonuses go on top, so a set can be priced without wearing it);
// the target side is typed by hand or filled from a monster's record. Edits apply as
// they are made and last as long as the finder that owns this.
public sealed partial class ItemFinderEstimatesViewModel : ObservableObject, IDialogViewModel<bool>
{
    // The character's stats with nothing worn, as the finder read them at open.
    public readonly record struct CharacterDefaults(
        int Level, int Strength, int Agility, int Intellect, int Charm, int Stealth);

    public event Action<bool>? CloseRequested;

    // An input the damage readout depends on moved.
    public event Action? Changed;

    private readonly CharacterDefaults _defaults;
    private readonly RealmType _realm;
    private readonly Dictionary<string, MonsterCatalogEntry> _monsterByLabel = new(StringComparer.Ordinal);
    // The record the target numbers were last filled from; they stop being "that
    // monster" the moment one of them is edited (see TargetText).
    private MonsterCatalogEntry? _lookedUp;
    private MudAttackType _attack = MudAttackType.Normal;
    // Set while several inputs change as one edit, so the readout recomputes once.
    private bool _batching;

    public IReadOnlyList<string> MonsterNames { get; }

    // ----- Character, with nothing worn -----
    [ObservableProperty] private int _level;
    [ObservableProperty] private int _strength;
    [ObservableProperty] private int _agility;
    [ObservableProperty] private int _intellect;
    [ObservableProperty] private int _charm;
    [ObservableProperty] private int _stealth;

    // ----- Target -----
    [ObservableProperty] private string? _selectedMonsterName;
    [ObservableProperty] private int _monsterAc;
    [ObservableProperty] private int _monsterDr;
    [ObservableProperty] private int _monsterDodge;
    [ObservableProperty] private int _monsterBsDefense;
    [ObservableProperty] private string _monsterNote = string.Empty;

    // ----- Which inputs the selected attack reads -----
    [ObservableProperty] private string _attackSummary = string.Empty;
    [ObservableProperty] private bool _usesStrength = true;
    [ObservableProperty] private bool _usesIntellectAndCharm = true;
    [ObservableProperty] private bool _usesStealth;

    public bool HasMonsterNote => MonsterNote.Length > 0;

    public ItemFinderEstimatesViewModel(CharacterDefaults defaults, RealmType realm, MonsterCatalog? monsters)
    {
        _defaults = defaults;
        _realm = realm;
        if (monsters is not null)
            foreach (MonsterCatalogEntry m in monsters.All)
                if (!string.IsNullOrEmpty(m.Name))
                    _monsterByLabel[string.Create(CultureInfo.InvariantCulture, $"{m.Name} (#{m.Number})")] = m;
        MonsterNames = _monsterByLabel.Keys.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ToList();

        _batching = true;
        ApplyDefaults();
        _batching = false;
        SetAttack(MudAttackType.Normal, "Attack");
    }

    public AttackTarget Target => new(MonsterAc, MonsterDr, MonsterDodge, MonsterBsDefense);

    // "vs cave bear (AC 40, DR 5, dodge 0)" for the readout's heading. The name only
    // shows while the numbers are still the record's.
    public string TargetText
    {
        get
        {
            string numbers = string.Create(CultureInfo.InvariantCulture,
                $"AC {MonsterAc}, DR {MonsterDr}, dodge {MonsterDodge}");
            if (_attack == MudAttackType.Backstab)
                numbers += string.Create(CultureInfo.InvariantCulture, $", BS defence {MonsterBsDefense}");
            if (_lookedUp is { } m && Target == TargetOf(m)) return $"vs {m.Name} ({numbers})";
            return Target == default ? "vs no target: no armour, dodge or resist to get past" : $"vs {numbers}";
        }
    }

    // Tell the window which attack the readout is pricing, so it shows only the
    // stats that attack reads.
    public void SetAttack(MudAttackType attack, string label)
    {
        _attack = attack;
        bool martialArts = attack is MudAttackType.Punch or MudAttackType.Kick or MudAttackType.Jumpkick;
        // Paradigm's strikes take nothing from Strength; its plain-attack accuracy
        // reads Intellect and Charm, which Stock's only counts toward crit.
        UsesStrength = !(martialArts && _realm == RealmType.ParaMud);
        UsesIntellectAndCharm = attack == MudAttackType.Normal || (martialArts && _realm == RealmType.ParaMud);
        UsesStealth = attack == MudAttackType.Backstab;
        AttackSummary = $"What {label} reads, with nothing worn. The trial set's bonuses are added on top.";
        RefreshMonsterNote();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(Level) or nameof(Strength) or nameof(Agility) or nameof(Intellect)
                or nameof(Charm) or nameof(Stealth)
                or nameof(MonsterAc) or nameof(MonsterDr) or nameof(MonsterDodge) or nameof(MonsterBsDefense):
                if (!_batching) Changed?.Invoke();
                break;
            case nameof(MonsterNote):
                OnPropertyChanged(nameof(HasMonsterNote));
                break;
        }
    }

    // A picked name fills the four numbers; text that names no monster leaves them
    // alone, so typing in the box never wipes hand-entered values.
    partial void OnSelectedMonsterNameChanged(string? value)
    {
        if (value is null || !_monsterByLabel.TryGetValue(value, out MonsterCatalogEntry? m)) return;
        _lookedUp = m;
        AttackTarget t = TargetOf(m);
        _batching = true;
        MonsterAc = t.ArmourClass;
        MonsterDr = t.DamageResist;
        MonsterDodge = t.Dodge;
        MonsterBsDefense = t.BsDefense;
        _batching = false;
        RefreshMonsterNote();
        Changed?.Invoke();
    }

    private static AttackTarget TargetOf(MonsterCatalogEntry m) =>
        new(m.ArmourClass, m.DamageResist, m.Dodge, m.BsDefense);

    // GAME_MECHANICS "Backstab": a see-hidden monster spots the sneak, so the stab
    // the readout prices can't be made against it.
    private void RefreshMonsterNote() =>
        MonsterNote = _attack == MudAttackType.Backstab && _lookedUp is { SeesHidden: true } m
            ? $"{m.Name} sees hidden, so a backstab can't open on it."
            : string.Empty;

    [RelayCommand]
    private void ResetCharacter()
    {
        _batching = true;
        ApplyDefaults();
        _batching = false;
        Changed?.Invoke();
    }

    private void ApplyDefaults()
    {
        Level = _defaults.Level;
        Strength = _defaults.Strength;
        Agility = _defaults.Agility;
        Intellect = _defaults.Intellect;
        Charm = _defaults.Charm;
        Stealth = _defaults.Stealth;
    }

    [RelayCommand]
    private void ClearTarget()
    {
        _lookedUp = null;
        _batching = true;
        SelectedMonsterName = null;
        MonsterAc = MonsterDr = MonsterDodge = MonsterBsDefense = 0;
        _batching = false;
        RefreshMonsterNote();
        Changed?.Invoke();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(false);
}
