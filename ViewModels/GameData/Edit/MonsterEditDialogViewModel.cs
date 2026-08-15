using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// View-model for the Game Data Browser → Monsters tab's per-record edit dialog.
// Surfaces the editable overlay fields (Use-tier, Name, Relationship, Priority, override
// spell slots, DontBackstab, KillOnSight).
//
// This dialog no longer edits any per-monster combat-message data: hit / miss / dodge /
// death are recognized generically (Game.Combat.CombatLineClassifier + MonsterDeathWatcher),
// and the flavor-adjective vocabulary is a shared per-set list edited in the browser's
// Flavor Prefixes section (Services.FlavorPrefixStore), not per-monster.
public sealed partial class MonsterEditDialogViewModel : ObservableObject, IDialogViewModel<MonsterEditResult>
{
    public event Action<MonsterEditResult?>? CloseRequested;

    public string WccNoStr { get; }
    public int    MonsterNumber { get; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private SettingsTier _useTier = SettingsTier.Character;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowKillOnSight))]
    private MonsterRelationship _relationship = MonsterRelationship.Enemy;
    [ObservableProperty] private MonsterAttackPriority _priority = MonsterAttackPriority.Normal;

    [ObservableProperty] private string _preAttackSpellId = string.Empty;
    [ObservableProperty] private string _preAttackCount = string.Empty;
    // "Override Attack" holds a Spell.Number, OR a spell cast-code that resolves
    // to one (both land on the mana-gated spell rung; Max is an optional per-room
    // cap, blank = unlimited), OR a raw verb like "attack"/"bash" that doesn't
    // resolve to any spell (sent as-is, no gating). See ParseAttackOverride.
    [ObservableProperty] private string _attackOverride = string.Empty;
    [ObservableProperty] private string _attackCount = string.Empty;

    [ObservableProperty] private bool _dontBackstab;

    // Kill this NEUTRAL monster on sight. Neutrals never attack first, so they're
    // normally left alone; checking this makes auto-combat engage it like an enemy
    // while other passive neutrals stay safe to rest among. Only meaningful for a
    // Neutral relationship — the checkbox is shown only then (ShowKillOnSight).
    [ObservableProperty] private bool _killOnSight;

    // The KillOnSight checkbox applies only to Neutral-relationship monsters.
    public bool ShowKillOnSight => Relationship == MonsterRelationship.Neutral;

    public IReadOnlyList<MdbInfoRow> MdbInfo { get; }

    public IReadOnlyList<MonsterRelationship> AvailableRelationships { get; } =
        Enum.GetValues<MonsterRelationship>().ToArray();

    public IReadOnlyList<MonsterAttackPriority> AvailablePriorities { get; } =
        Enum.GetValues<MonsterAttackPriority>().ToArray();

    // Tiers the picker offers. Restricted to tiers the resolver can actually write to in
    // the current session (Global always; BBS only with an active BBS; Character only with
    // a loaded profile) so Save can't land on a tier whose scope is unresolvable.
    // Read-only Defaults is excluded — the MDB is its source.
    public IReadOnlyList<SettingsTier> AvailableTiers { get; }

    public string Title => $"Monster — {(Name.Length > 0 ? Name : $"#{WccNoStr}")}";

    // Resolves a typed cast-code (e.g. "turn") to its Spells.Number, or null
    // when the text doesn't match a known spell — see ParseAttackOverride.
    // Optional so the dialog still works (falling back to numeric-only
    // detection) wherever game data isn't wired, e.g. tests.
    private readonly Func<string, int?>? _resolveSpellShort;

    // The inverse: a stored Spells.Number back to its cast-code, so re-opening
    // the dialog on an override that auto-resolved from a typed code (e.g.
    // "agon") shows "agon" again, not the internal number it resolved to
    // (report paradigm-20260813-131658: "it keeps putting 22 in when I put the
    // spell in"). Falls back to the raw number when unresolvable (e.g. the
    // game-data set changed since the override was saved).
    private readonly Func<int, string?>? _resolveSpellNumber;

    public MonsterEditDialogViewModel(
        string wccNoStr,
        string mdbName,
        MonsterOverlay? existing,
        SettingsTier currentTier,
        IReadOnlyList<MdbInfoRow> mdbInfo,
        IReadOnlyList<SettingsTier>? writableTiers = null,
        Func<string, int?>? resolveSpellShort = null,
        Func<int, string?>? resolveSpellNumber = null)
    {
        _resolveSpellShort = resolveSpellShort;
        _resolveSpellNumber = resolveSpellNumber;
        WccNoStr      = wccNoStr;
        MonsterNumber = int.TryParse(wccNoStr, out int n) ? n : 0;
        Name          = existing?.Name ?? mdbName;
        AvailableTiers = writableTiers is { Count: > 0 }
            ? writableTiers
            : Enum.GetValues<SettingsTier>().ToArray();
        // Default to the tier the existing override lives at when it's writable;
        // otherwise the most-specific writable tier (first in the list). Stops
        // the picker defaulting to read-only Defaults or an unloaded Character,
        // which made Save throw "no named profile loaded" and crash the app.
        UseTier       = AvailableTiers.Contains(currentTier) ? currentTier : AvailableTiers[0];
        MdbInfo       = mdbInfo;

        Relationship = existing?.Relationship ?? MonsterRelationship.Enemy;
        Priority     = existing?.Priority     ?? MonsterAttackPriority.Normal;

        PreAttackSpellId = (existing?.OverridePreAttackSpellId is { } pi) ? pi.ToString() : string.Empty;
        PreAttackCount   = (existing?.OverridePreAttackCount   is { } pc) ? pc.ToString() : string.Empty;
        // A command override wins the box display; else show the spell's cast-code
        // when it resolves (round-trips a typed "agon" back to "agon", not its
        // internal number), falling back to the bare number when it doesn't.
        AttackOverride   = existing?.OverrideAttackCommand is { Length: > 0 } cmd
            ? cmd
            : (existing?.OverrideAttackSpellId is { } ai
                ? (_resolveSpellNumber?.Invoke(ai) ?? ai.ToString())
                : string.Empty);
        AttackCount      = (existing?.OverrideAttackCount      is { } ac) ? ac.ToString() : string.Empty;

        DontBackstab = existing?.DontBackstab ?? false;
        KillOnSight  = existing?.KillOnSight  ?? false;
    }

    [RelayCommand]
    private void Save()
    {
        (int? attackSpellId, string? attackCommand) = ParseAttackOverride(AttackOverride, _resolveSpellShort);
        MonsterOverlay overlay = new()
        {
            Name                     = string.IsNullOrWhiteSpace(Name) ? null : Name,
            Relationship             = Relationship,
            Priority                 = Priority,
            OverridePreAttackSpellId = ParseNullableInt(PreAttackSpellId),
            OverridePreAttackCount   = ParseNullableInt(PreAttackCount),
            OverrideAttackSpellId    = attackSpellId,
            OverrideAttackCount      = ParseNullableInt(AttackCount),
            OverrideAttackCommand    = attackCommand,
            DontBackstab             = DontBackstab,
            KillOnSight              = KillOnSight,
        };

        CloseRequested?.Invoke(new MonsterEditResult(WccNoStr, overlay, UseTier));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    private static int? ParseNullableInt(string? text)
        => int.TryParse(text, out int n) ? n : null;

    // The "Override Attack" box holds EITHER a Spell.Number (routed through the
    // mana-gated attack-spell rung — needs a Max cast count) OR a raw command /
    // verb like "attack" or "bash" (sent verbatim, no gating). A positive
    // integer reads as a spell id directly; blank is no override.
    //
    // For any other text, resolveSpellShort (when supplied) gets first look: a
    // typed cast-code that matches a known spell (e.g. "turn") resolves to that
    // spell's Number and lands on the SAME mana-gated, cascading rung as typing
    // the number directly — someone reasonably types the code they'd actually
    // cast in-game, not an internal database id they have no way to know,
    // and shouldn't silently lose mana/cap gating for it (report
    // paradigm-20260813-070249: "it just means you use a different spell",
    // not "ignore combat settings completely"). Only text that resolves to no
    // known spell falls through to the raw command path — this is also why
    // typing "attack" persists as a command instead of being silently dropped
    // by an int-only parse (report paradigm-20260809-131642).
    //
    // Exactly one of the pair is set (or both null) — the two are kept mutually
    // exclusive so a species never carries both an id and a command.
    public static (int? SpellId, string? Command) ParseAttackOverride(
        string? text, Func<string, int?>? resolveSpellShort = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        string trimmed = text.Trim();
        if (int.TryParse(trimmed, out int n) && n > 0) return (n, null);
        if (resolveSpellShort?.Invoke(trimmed) is { } resolved) return (resolved, null);
        return (null, trimmed);
    }
}

// Returned by MonsterEditDialogViewModel on Save. WccNoStr is the monster's WCC No as a
// string — primary key for the overlay write; Overlay is the user's edited overlay
// payload; Tier is the tier the overlay should be written at.
public sealed record MonsterEditResult(
    string         WccNoStr,
    MonsterOverlay Overlay,
    SettingsTier   Tier);
