using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.Settings;

// The Settings window's shared staging buffer for the combat-profile list. A combat
// profile spans several Settings tabs — Combat, Health and Spells edit ONE staged
// working list through this session instead of each keeping its own; the Party tab
// isn't staged and writes its live section ahead of the commit (Committing). Nothing here touches the character profile until Commit;
// the window's Cancel path drops the edits (DiscardAndReset re-clones the persisted
// state).
//
// Each participating tab wires a Capture handler (fold its own boxes into the active
// working profile) and Load handlers (load the active working profile into its
// boxes). A chip switch / add / remove folds every tab's boxes into the outgoing
// profile, moves the active pointer, then reloads every tab from the incoming one.
// Commit folds once and persists all of it — both Settings sections, the
// CombatProfiles blob, and the Default-set weapons — in a single Save, guarded so
// calling it from each dirty tab's Apply commits only once.
//
// Lives for one Settings-window lifetime: created + owned by SettingsWindowViewModel
// and passed to the Combat + Health section VMs. It subscribes to ProfileLoaded so a
// mid-window character swap re-seeds and reloads both tabs (its handler runs before
// the section VMs' because it is constructed first).
public sealed class CombatProfileStagingSession : IDisposable
{
    private readonly CombatProfileManager _mgr;
    private readonly ProfileService _profile;
    private readonly Func<EquipmentSettings?> _equipment;

    private List<CombatSpellProfile> _profiles = new();
    private int _active;
    private bool _dirty;
    // Staged "Include in combat profile" state: the groups taken out of profiles.
    private HashSet<CombatProfileGroup> _shared = new();

    // Set by the Combat tab: builds the FULL Settings["Combat"] DTO — the shared
    // fields (targeting / backstab / action-order / display) plus the active
    // profile's per-profile fields — from its live boxes. Required before the first
    // Commit; both tabs are constructed before any commit can run.
    public Func<CombatSettings>? BuildFullCombat { get; set; }

    // Set by the Spells tab: builds the FULL Settings["Spells"] DTO — the
    // per-character fields (cures / bless timing / ailment gates) plus the active
    // profile's priority + heal subset — from its live boxes. Null when the Spells
    // tab isn't wired (standalone session); the commit then leaves Settings["Spells"].
    public Func<SpellsSettings>? BuildFullSpells { get; set; }

    // Fold a participating tab's boxes into the active working profile (mutate in
    // place — never replace the object, or one tab clobbers the other's fold).
    public event Action? CaptureRequested;
    // Chip switch / add / remove: load the active working profile's PER-PROFILE
    // fields into a tab's boxes, leaving that tab's shared fields untouched.
    public event Action? LoadRequested;
    // Profile swap / discard: reload EVERYTHING (shared + per-profile) from the
    // freshly (re)seeded working list.
    public event Action? ReloadAllRequested;
    // The list / active pointer / a name changed — rebuild the chip bar.
    public event Action? ChipsChanged;
    // A group's "Include in combat profile" state changed (or was reseeded).
    public event Action? SharedGroupsChanged;
    // About to commit — a tab outside the staged list (Party) writes its pending
    // edits first, so the commit folds them into the right profile.
    public event Action? Committing;
    // Committed — tabs clear their own dirty flags.
    public event Action? Committed;

    public CombatProfileStagingSession(
        CombatProfileManager mgr, ProfileService profile, Func<EquipmentSettings?> equipment)
    {
        _mgr = mgr ?? throw new ArgumentNullException(nameof(mgr));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _equipment = equipment ?? throw new ArgumentNullException(nameof(equipment));
        Reseed();
        _profile.ProfileLoaded += OnProfileReloaded;
        _profile.ProfileClosed += OnProfileClosed;
    }

    public void Dispose()
    {
        _profile.ProfileLoaded -= OnProfileReloaded;
        _profile.ProfileClosed -= OnProfileClosed;
    }

    public IReadOnlyList<CombatSpellProfile> Profiles => _profiles;
    public int ActiveIndex => _active;

    // Always ≥1 profile (the manager guarantees it; Reseed adds a blank fallback).
    public CombatSpellProfile Active => _profiles[_active];
    public bool IsDirty => _dirty;

    // (Re)clone the working list from the persisted manager state, resetting the
    // active pointer + dirty flag. Callers fire ReloadAllRequested afterward.
    private void Reseed()
    {
        _profiles = _mgr.Profiles.Select(p => p.Clone(newIdentity: false)).ToList();
        if (_profiles.Count == 0) _profiles.Add(new CombatSpellProfile());
        int active = _mgr.ActiveIndex;
        _active = Math.Clamp(active < 0 ? 0 : active, 0, _profiles.Count - 1);
        _shared = new HashSet<CombatProfileGroup>(_profile.Current?.CombatProfiles?.SharedGroups ?? new());
        _dirty = false;
    }

    // Whether a group is shared by every profile ("Include in combat profile" off).
    public bool IsShared(CombatProfileGroup group) => _shared.Contains(group);

    // Stage a group in or out of combat profiles. Taking it out makes the active
    // profile's values the one shared value: fold the tabs first so those are the
    // values on screen, then copy them into every working profile. Putting it back
    // changes nothing — every profile starts from the shared value.
    public void SetShared(CombatProfileGroup group, bool shared)
    {
        if (IsShared(group) == shared) return;
        if (shared)
        {
            CaptureRequested?.Invoke();
            _shared.Add(group);
            CombatProfileGroupCopy.SyncShared(new[] { group }, Active, _profiles);
        }
        else
        {
            _shared.Remove(group);
        }
        _dirty = true;
        SharedGroupsChanged?.Invoke();
    }

    private void OnProfileReloaded(CharacterProfile _) => ReseedAndReloadTabs();
    private void OnProfileClosed() => ReseedAndReloadTabs();

    private void ReseedAndReloadTabs()
    {
        Reseed();
        ChipsChanged?.Invoke();
        SharedGroupsChanged?.Invoke();
        ReloadAllRequested?.Invoke();
    }

    // Any per-profile OR shared-field edit on either tab marks the whole staged
    // surface dirty — the Commit writes both Settings sections + the blob + weapons
    // as one unit, so it re-fires for any of them.
    public void MarkDirty() => _dirty = true;

    public void SwitchTo(int index)
    {
        if (index < 0 || index >= _profiles.Count || index == _active) return;
        CaptureRequested?.Invoke();
        CombatProfileGroupCopy.SyncShared(_shared, Active, new[] { _profiles[index] });
        _active = index;
        LoadRequested?.Invoke();
        _dirty = true;
        ChipsChanged?.Invoke();
    }

    // Stage a new EMPTY profile and switch to it (blank fields clear the boxes).
    public void AddNew()
    {
        CaptureRequested?.Invoke();
        CombatSpellProfile added = new();
        CombatProfileGroupCopy.SyncShared(_shared, Active, new[] { added });
        _profiles.Add(added);
        _active = _profiles.Count - 1;
        LoadRequested?.Invoke();
        _dirty = true;
        ChipsChanged?.Invoke();
    }

    // Remove the active profile (kept ≥1) and switch to a neighbour.
    public void RemoveActive()
    {
        if (_profiles.Count <= 1) return;
        _profiles.RemoveAt(_active);
        if (_active >= _profiles.Count) _active = _profiles.Count - 1;
        LoadRequested?.Invoke();
        _dirty = true;
        ChipsChanged?.Invoke();
    }

    // Rename the active profile (the name box below the chips). Refreshes the chip
    // labels on structural events; live keystrokes just mark dirty.
    public void SetActiveName(string? name)
    {
        string n = name ?? string.Empty;
        if (Active.Name == n) return;
        Active.Name = n;
        _dirty = true;
    }

    // Persist everything as one unit — fold both tabs once, then write Settings
    // ["Combat"] + Settings["Health"] + the CombatProfiles blob + the Default-set
    // weapons in a single Save. Guarded on _dirty so calling it from each dirty
    // section's Apply commits only once per ApplyAll cycle.
    public void CommitIfDirty()
    {
        if (!_dirty) return;
        if (_profile.Current is not { } profile) { _dirty = false; return; }

        Committing?.Invoke();
        CaptureRequested?.Invoke();   // fold BOTH tabs into the working profiles

        // The Party tab isn't staged here: the live Settings["Party"] belongs to the
        // profile that was active when the window opened. Fold it into that profile's
        // working copy, then keep every shared group identical across profiles — the
        // Party groups from that profile, the rest from the active one's boxes.
        string? persistedActiveId = _mgr.Active?.Id;
        CombatSpellProfile partySource =
            _profiles.FirstOrDefault(p => p.Id == persistedActiveId) ?? Active;
        _mgr.CapturePartyInto(partySource);
        foreach (CombatProfileGroup g in _shared)
            CombatProfileGroupCopy.SyncShared(new[] { g }, IsPartyGroup(g) ? partySource : Active, _profiles);

        bool sharedChanged = !_shared.SetEquals(_mgr.SharedGroups);

        profile.Settings ??= new();
        if (BuildFullCombat is { } build)
            profile.Settings["Combat"] = JsonSerializer.SerializeToElement(build());
        if (BuildFullSpells is { } buildSpells)
            profile.Settings["Spells"] = JsonSerializer.SerializeToElement(buildSpells());
        profile.Settings["Health"] = JsonSerializer.SerializeToElement(Active.Health.Clone());
        // Carry the stored schema version: a blob written back at version 0 re-runs the
        // one-time back-fills on the next load, overwriting every profile's own values
        // with the active one's.
        profile.CombatProfiles = new CombatProfileSettings
        {
            Profiles = _profiles.Select(p => p.Clone(newIdentity: false)).ToList(),
            ActiveId = Active.Id,
            SchemaVersion = profile.CombatProfiles?.SchemaVersion ?? 0,
            SharedGroups = _shared.Count > 0 ? _shared.OrderBy(g => g).ToList() : null,
        };
        if (_equipment() is { } eq) EquipmentWeaponSync.WriteProfileWeapons(eq, Active);
        // A chip switch in the window moved the active profile: its Party-tab subset
        // goes live too, the way a switch from the toolbar does it.
        if (!ReferenceEquals(partySource, Active)) _mgr.WritePartyFrom(Active);
        _profile.Save();
        if (sharedChanged) _mgr.LogSharedGroups();

        _dirty = false;
        _mgr.RaiseChanged();      // refresh Action-menu / toolbar / Workshop marking from the committed state
        Committed?.Invoke();
    }

    private static bool IsPartyGroup(CombatProfileGroup g) =>
        g is CombatProfileGroup.PartyHealing or CombatProfileGroup.PartyBless;

    // Drop staged edits — re-clone from persisted and reload both tabs.
    public void DiscardAndReset() => ReseedAndReloadTabs();
}
