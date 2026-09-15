using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.GameData;
using MudPlay.Models.Settings;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Batch;

// The set of monster-override changes a batch commits, and the pure rule for
// folding them onto one record's existing overlay. Only the opted-in fields are
// touched — every other field on each record round-trips untouched, so batching
// Relationship never wipes a monster's existing Priority override.
public sealed class MonsterBatchChanges
{
    public bool ChangeRelationship { get; init; }
    public MonsterRelationship Relationship { get; init; }

    public bool ChangePriority { get; init; }
    public MonsterAttackPriority Priority { get; init; }

    public BatchToggle DontBackstab { get; init; } = BatchToggle.Leave;
    public BatchToggle KillOnSight { get; init; } = BatchToggle.Leave;

    public bool ChangePhysicalCommand { get; init; }
    public string PhysicalCommand { get; init; } = string.Empty;

    // Spell rungs: when the rung is opted in, SpellId null clears it (blank box) and
    // a value sets it along with the rung's cap + mana floor.
    public bool ChangePreAttack { get; init; }
    public int? PreAttackSpellId { get; init; }
    public int? PreAttackCount { get; init; }
    public int PreAttackMinMana { get; init; }

    public bool ChangeNormalAttack { get; init; }
    public int? NormalSpellId { get; init; }
    public int? NormalCount { get; init; }
    public int NormalMinMana { get; init; }

    public bool ChangeAltAttack { get; init; }
    public int? AltSpellId { get; init; }
    public int? AltCount { get; init; }
    public int AltMinMana { get; init; }

    public bool AnyFieldChosen =>
        ChangeRelationship || ChangePriority || ChangePhysicalCommand
        || DontBackstab != BatchToggle.Leave || KillOnSight != BatchToggle.Leave
        || ChangePreAttack || ChangeNormalAttack || ChangeAltAttack;

    public MonsterOverlay ApplyTo(MonsterOverlay existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        MonsterOverlay o = existing;
        if (ChangeRelationship) o = o with { Relationship = Relationship };
        if (ChangePriority) o = o with { Priority = Priority };
        if (DontBackstab != BatchToggle.Leave) o = o with { DontBackstab = DontBackstab.Apply(o.DontBackstab) };
        if (KillOnSight != BatchToggle.Leave) o = o with { KillOnSight = KillOnSight.Apply(o.KillOnSight) };
        if (ChangePhysicalCommand)
            o = o with { OverridePhysicalCommand = string.IsNullOrWhiteSpace(PhysicalCommand) ? null : PhysicalCommand.Trim() };
        if (ChangePreAttack)
            o = o with { OverridePreAttackSpellId = PreAttackSpellId, OverridePreAttackCount = PreAttackSpellId is null ? null : NullIfZero(PreAttackCount), OverridePreAttackMinMana = PreAttackSpellId is null ? null : NullIfZero(PreAttackMinMana) };
        if (ChangeNormalAttack)
            o = o with { OverrideAttackSpellId = NormalSpellId, OverrideAttackCount = NormalSpellId is null ? null : NullIfZero(NormalCount), OverrideAttackMinMana = NormalSpellId is null ? null : NullIfZero(NormalMinMana) };
        if (ChangeAltAttack)
            o = o with { OverrideAltAttackSpellId = AltSpellId, OverrideAltAttackCount = AltSpellId is null ? null : NullIfZero(AltCount), OverrideAltAttackMinMana = AltSpellId is null ? null : NullIfZero(AltMinMana) };
        return o;
    }

    private static int? NullIfZero(int? v) => v is > 0 ? v : null;
    private static int? NullIfZero(int v) => v > 0 ? v : null;
}

// Batch result: the tier to write at, plus the changes to fold onto each record.
// A Defaults tier means "reset the selected records" — Changes is ignored there.
public sealed record MonsterBatchResult(SettingsTier Tier, MonsterBatchChanges Changes);

// Batch editor for the Monsters table. Every field has a "Change" opt-in (or a
// tri-state Leave/On/Off for the flags); on Apply the section folds the chosen
// fields onto each selected monster's existing overlay at the chosen tier.
public sealed partial class MonsterBatchEditDialogViewModel
    : ObservableObject, IDialogViewModel<MonsterBatchResult>
{
    public event Action<MonsterBatchResult?>? CloseRequested;

    private readonly Func<string, int?>? _resolveSpellShort;

    public int Count { get; }
    public string Heading { get; }

    public IReadOnlyList<SettingsTier> AvailableTiers { get; }
    public IReadOnlyList<MonsterRelationship> AvailableRelationships { get; } = Enum.GetValues<MonsterRelationship>().ToArray();
    public IReadOnlyList<MonsterAttackPriority> AvailablePriorities { get; } = Enum.GetValues<MonsterAttackPriority>().ToArray();
    public IReadOnlyList<BatchToggle> ToggleOptions { get; } = new[] { BatchToggle.Leave, BatchToggle.On, BatchToggle.Off };

    [ObservableProperty] private SettingsTier _useTier;

    [ObservableProperty] private bool _changeRelationship;
    [ObservableProperty] private MonsterRelationship _relationship = MonsterRelationship.Enemy;
    [ObservableProperty] private bool _changePriority;
    [ObservableProperty] private MonsterAttackPriority _priority = MonsterAttackPriority.Normal;
    [ObservableProperty] private BatchToggle _dontBackstab = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _killOnSight = BatchToggle.Leave;
    [ObservableProperty] private bool _changePhysicalCommand;
    [ObservableProperty] private string _physicalCommand = string.Empty;

    [ObservableProperty] private bool _changePreAttack;
    [ObservableProperty] private string _preAttackSpell = string.Empty;
    [ObservableProperty] private int? _preAttackCount;
    [ObservableProperty] private int _preAttackMinMana;
    [ObservableProperty] private bool _changeNormalAttack;
    [ObservableProperty] private string _normalSpell = string.Empty;
    [ObservableProperty] private int? _normalCount;
    [ObservableProperty] private int _normalMinMana;
    [ObservableProperty] private bool _changeAltAttack;
    [ObservableProperty] private string _altSpell = string.Empty;
    [ObservableProperty] private int? _altCount;
    [ObservableProperty] private int _altMinMana;

    public bool IsResetTier => UseTier == SettingsTier.Defaults;

    public MonsterBatchEditDialogViewModel(
        int count,
        IReadOnlyList<SettingsTier>? writableTiers = null,
        Func<string, int?>? resolveSpellShort = null)
    {
        _resolveSpellShort = resolveSpellShort;
        Count = count;
        Heading = $"Batch edit {count} monsters";
        IReadOnlyList<SettingsTier> writable = writableTiers is { Count: > 0 }
            ? writableTiers
            : new[] { SettingsTier.Character, SettingsTier.Bbs, SettingsTier.Global };
        AvailableTiers = writable.Append(SettingsTier.Defaults).ToArray();
        UseTier = writable[0];
    }

    partial void OnUseTierChanged(SettingsTier value) => OnPropertyChanged(nameof(IsResetTier));

    private int? ResolveSpell(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (_resolveSpellShort?.Invoke(text) is { } id) return id;
        return int.TryParse(text, out int n) ? n : null;
    }

    private MonsterBatchChanges BuildChanges() => new()
    {
        ChangeRelationship = ChangeRelationship,
        Relationship = Relationship,
        ChangePriority = ChangePriority,
        Priority = Priority,
        DontBackstab = DontBackstab,
        KillOnSight = KillOnSight,
        ChangePhysicalCommand = ChangePhysicalCommand,
        PhysicalCommand = PhysicalCommand,
        ChangePreAttack = ChangePreAttack,
        PreAttackSpellId = ResolveSpell(PreAttackSpell),
        PreAttackCount = PreAttackCount,
        PreAttackMinMana = PreAttackMinMana,
        ChangeNormalAttack = ChangeNormalAttack,
        NormalSpellId = ResolveSpell(NormalSpell),
        NormalCount = NormalCount,
        NormalMinMana = NormalMinMana,
        ChangeAltAttack = ChangeAltAttack,
        AltSpellId = ResolveSpell(AltSpell),
        AltCount = AltCount,
        AltMinMana = AltMinMana,
    };

    [RelayCommand]
    private void Apply() => CloseRequested?.Invoke(new MonsterBatchResult(UseTier, BuildChanges()));

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
