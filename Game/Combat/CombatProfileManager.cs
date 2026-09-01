using System;
using System.Collections.Generic;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Owns the character's casting-spell profiles (CharacterProfile.CombatProfiles):
// the named list, which one is active, and the CRUD + quick-swap operations. A
// switch overlays the target profile's spell fields onto the live Combat section
// (Settings["Combat"]) and persists; the combat engine re-reads that section every
// round, so a swap takes effect on the next round with no restart. Non-spell
// combat settings stay shared across profiles.
//
// Invariant: there is always at least one profile, and the active profile's spell
// fields mirror the live Combat section (both change only on the Combat tab's Save
// — via CaptureActiveFrom — or a switch here). Fires Changed on any list / active
// change so the Settings chips and toolbar buttons refresh.
public sealed class CombatProfileManager
{
    private readonly Func<CharacterProfile?> _profile;
    private readonly Func<CombatSettings> _readCombat;
    private readonly Action<CombatSettings> _writeCombat;   // serialize Settings["Combat"] + Save
    private readonly Action _save;                           // Save only (metadata-only changes)
    private readonly LogService? _log;

    public event Action? Changed;

    // Late-bound terminal echo — set by MainWindowViewModel to WriteTerminalStatus.
    // Every switch (UI chip / toolbar / Action menu / @profile) reports through it,
    // so the local player always sees which profile is now live and its slots.
    public Action<string>? Announce { get; set; }

    public CombatProfileManager(
        Func<CharacterProfile?> profile,
        Func<CombatSettings> readCombat,
        Action<CombatSettings> writeCombat,
        Action save,
        LogService? log = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _readCombat = readCombat ?? throw new ArgumentNullException(nameof(readCombat));
        _writeCombat = writeCombat ?? throw new ArgumentNullException(nameof(writeCombat));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _log = log;
    }

    // Seed a first profile from the current combat settings when none exist, and
    // repair a stale active pointer. Call on construction and every ProfileLoaded.
    public void EnsureSeeded()
    {
        if (_profile() is not { } p) return;
        CombatProfileSettings store = p.CombatProfiles ??= new CombatProfileSettings();
        bool changed = false;
        if (store.Profiles.Count == 0)
        {
            store.Profiles.Add(CombatSpellProfile.Capture(string.Empty, _readCombat()));
            store.ActiveId = store.Profiles[0].Id;
            changed = true;
            _log?.Log(LogSeverity.Info, "CombatProfiles", "Seeded first combat profile from live combat settings");
        }
        else if (IndexOfActive(store) < 0)
        {
            store.ActiveId = store.Profiles[0].Id;
            changed = true;
            _log?.Log(LogSeverity.Info, "CombatProfiles",
                $"Active combat profile pointer was stale; reset to profile 1 of {store.Profiles.Count}");
        }
        if (changed) _save();
        Changed?.Invoke();
    }

    public IReadOnlyList<CombatSpellProfile> Profiles =>
        Store()?.Profiles ?? (IReadOnlyList<CombatSpellProfile>)Array.Empty<CombatSpellProfile>();

    // 0-based index of the active profile, or -1 when no profile is loaded.
    public int ActiveIndex
    {
        get
        {
            CombatProfileSettings? s = Store();
            if (s is null || s.Profiles.Count == 0) return -1;
            int i = IndexOfActive(s);
            return i >= 0 ? i : 0;
        }
    }

    public CombatSpellProfile? Active
    {
        get
        {
            int i = ActiveIndex;
            CombatProfileSettings? s = Store();
            return s is not null && i >= 0 && i < s.Profiles.Count ? s.Profiles[i] : null;
        }
    }

    // Switch to the profile at index — overlay its spells onto the live Combat
    // section, mark it active, persist, and return the one-line swap report (null
    // when the index is out of range / no profile loaded).
    public string? SwitchToIndex(int index)
    {
        CombatProfileSettings? s = Store();
        if (s is null || index < 0 || index >= s.Profiles.Count) return null;
        CombatSpellProfile target = s.Profiles[index];
        s.ActiveId = target.Id;
        CombatSettings combat = _readCombat();
        target.ApplyTo(combat);
        _writeCombat(combat);   // one Save persists Settings["Combat"] + the active pointer
        string report = CombatSpellProfileReport.Describe(target, index + 1);
        _log?.Log(LogSeverity.Info, "CombatProfiles", "Switched to " + report);
        _log?.Debug("CombatProfiles", CombatSpellProfileReport.DescribeConfig(target, index + 1));
        Announce?.Invoke(report);
        Changed?.Invoke();
        return report;
    }

    // Resolve @profile's argument (number or best-match name) and switch. Returns
    // the swap report, or null when nothing matched.
    public string? SwitchByArg(string? arg)
    {
        CombatProfileSettings? s = Store();
        if (s is null) return null;
        return CombatSpellProfileMatcher.Resolve(s.Profiles, arg) is { } i ? SwitchToIndex(i) : null;
    }

    // Advance to the next profile in order (wraps) — the toolbar cycle button's
    // left-click. Returns the swap report.
    public string? Cycle()
    {
        CombatProfileSettings? s = Store();
        if (s is null || s.Profiles.Count == 0) return null;
        return SwitchToIndex((Math.Max(0, IndexOfActive(s)) + 1) % s.Profiles.Count);
    }

    // Step back to the previous profile (wraps) — the cycle button's right-click.
    public string? CycleBack()
    {
        CombatProfileSettings? s = Store();
        if (s is null || s.Profiles.Count == 0) return null;
        int count = s.Profiles.Count;
        return SwitchToIndex((Math.Max(0, IndexOfActive(s)) - 1 + count) % count);
    }

    // Fire Changed without a state change — the Combat tab calls this after Apply
    // commits its staged profile list, so the Action-menu / toolbar pick up the
    // new set + active profile.
    public void RaiseChanged() => Changed?.Invoke();

    // The @profile query roster (no switch) — the active profile plus the others
    // on standby, e.g. "{Current: 1)Fire, On Standby: 2)Cold, 3)Lightning}". Null
    // when none is loaded.
    public string? RosterReport()
    {
        CombatProfileSettings? s = Store();
        if (s is null || s.Profiles.Count == 0) return null;
        return CombatSpellProfileReport.DescribeRoster(s.Profiles, ActiveIndex);
    }

    // The active profile's full-config line (slots + gates + knobs) — for the
    // combat-engage log anchor. Null when none is loaded.
    public string? CurrentConfigLine()
    {
        int i = ActiveIndex;
        CombatSpellProfile? a = Active;
        return a is not null && i >= 0 ? CombatSpellProfileReport.DescribeConfig(a, i + 1) : null;
    }

    private CombatProfileSettings? Store() => _profile()?.CombatProfiles;

    private static int IndexOfActive(CombatProfileSettings s)
    {
        for (int i = 0; i < s.Profiles.Count; i++)
            if (s.Profiles[i].Id == s.ActiveId) return i;
        return -1;
    }
}
