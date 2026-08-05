using System;
using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Persisted per-set boss kill-times. On a confirmed boss kill (a specific
// MonsterDied identity that matches a tracked boss AND lands in one of that boss's
// rooms) the kill time is stamped and written to {set}/boss-timers.json, so a long
// respawn timer survives an app restart. Timer VALUES aren't stored — the full
// respawn hours are resolved live from game data (BossCatalog), and the realm's
// early-window model comes from BossTimerMath. Realm-wide like BossStore: keyed to
// the active set, shared across the user's characters.
//
// Kill detection is deliberately conservative: only a positively-identified death
// (name or MonsterNumber match) in a boss room auto-starts a timer. A fallback
// death (exp + *Combat Off* with no identity) can't be attributed to a specific
// boss, so it's left to the tab's manual "mark killed" override rather than risk a
// false timer on trash killed in a boss room.
public sealed class BossTimerStore
{
    private readonly BossStore _bosses;
    private readonly GameDataCache _gameData;
    private readonly LogService? _log;

    // boss name (lowercase) -> UTC kill time.
    private Dictionary<string, DateTimeOffset> _killed =
        new(StringComparer.OrdinalIgnoreCase);

    public string? ActiveSet { get; private set; }

    // Fires after any change to the tracked set (kill stamped, reset, reload) so a
    // view can refresh its live status column off the store rather than polling.
    public event Action? Changed;

    public BossTimerStore(BossStore bosses, GameDataCache gameData, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(bosses);
        ArgumentNullException.ThrowIfNull(gameData);
        _bosses = bosses;
        _gameData = gameData;
        _log = log;
    }

    public void OnActiveSetChanged(string? setName)
    {
        ActiveSet = string.IsNullOrWhiteSpace(setName) ? null : setName;
        _killed = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        if (ActiveSet is not null &&
            JsonStore.Load<Dictionary<string, DateTimeOffset>>(AppPaths.BossTimersFile(ActiveSet)) is { } loaded)
        {
            foreach ((string name, DateTimeOffset at) in loaded) _killed[name] = at;
        }
        Changed?.Invoke();
    }

    public DateTimeOffset? KilledAt(string name)
        => _killed.TryGetValue(name, out DateTimeOffset at) ? at : null;

    // Stamp a kill at now (UTC) and persist. Used by auto-detection and the tab's
    // manual "mark killed" button.
    public void MarkKilled(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _killed[name] = DateTimeOffset.UtcNow;
        Persist();
        _log?.Info("Bosses", $"timer started for '{name}'");
        Changed?.Invoke();
    }

    // Clear a boss's timer (manual reset / mistaken start).
    public void Reset(string name)
    {
        if (_killed.Remove(name))
        {
            Persist();
            _log?.Info("Bosses", $"timer cleared for '{name}'");
            Changed?.Invoke();
        }
    }

    // Live window state for a boss, or null when it has no active timer (never
    // killed, already expired, a Cleanup boss, or no game-data respawn timer).
    public BossWindowState? StatusFor(BossDef def, RealmType realm)
    {
        ArgumentNullException.ThrowIfNull(def);
        if (def.RespawnType != BossRespawnType.Timed) return null;
        if (KilledAt(def.Name) is not { } killed) return null;
        if (BossCatalog.ResolveRegenHours(_gameData, def.Name) is not { } hours || hours <= 0) return null;
        BossWindowState state = BossTimerMath.Describe(realm, def.ExactSpawn, hours, DateTimeOffset.UtcNow - killed);
        return state.Expired ? null : state;
    }

    // Every boss on the given realm with a running timer, paired with its state,
    // soonest guaranteed spawn first. Drives the tab summary + @timer no-arg report.
    public IReadOnlyList<(BossDef Def, BossWindowState State)> ActiveTimers(RealmType realm)
    {
        var active = new List<(BossDef, BossWindowState)>();
        foreach (BossDef def in _bosses.ResolveForRealm(realm))
            if (StatusFor(def, realm) is { } state) active.Add((def, state));
        return active.OrderBy(a => a.Item2.FullRemaining).ToList();
    }

    // Auto-start on a positively-identified boss death in one of the boss's rooms.
    // key is the live RoomTracker.State.CurrentRoom.Key read at kill time (the death
    // event itself carries no location).
    public void OnMonsterDied(MonsterDeathEvent evt, RoomKey? key)
    {
        if (evt.IsFallback || evt.Candidates.Count == 0) return;   // no identity to attribute
        if (key is not { } here) return;                           // can't confirm placement

        foreach (BossDef def in _bosses.Resolve())
        {
            if (def.RespawnType != BossRespawnType.Timed) continue;
            if (!RoomsContain(def, here)) continue;
            foreach (MonsterDeathIdentity id in evt.Candidates)
            {
                bool numberMatch = def.MonsterNumber is { } n && id.Number == n;
                bool nameMatch = string.Equals(def.Name, id.Name, StringComparison.OrdinalIgnoreCase);
                if (numberMatch || nameMatch)
                {
                    MarkKilled(def.Name);
                    return;
                }
            }
        }
    }

    private static bool RoomsContain(BossDef def, RoomKey key)
    {
        foreach (string wire in def.Rooms)
            if (RoomKey.TryParseWire(wire, out RoomKey k) && k == key) return true;
        return false;
    }

    private void Persist()
    {
        if (ActiveSet is null) return;
        JsonStore.Save(AppPaths.BossTimersFile(ActiveSet), _killed);
    }
}
