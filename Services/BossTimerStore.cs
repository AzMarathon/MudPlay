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
// Kill detection matches the in-game signal: we were ENGAGED with a monster by
// name, then it died (awarded exp). Monster numbers aren't observable in-game, so
// attribution is by NAME — the engaged target name (CombatManager.CurrentTarget,
// read live at death) matched against the boss list, confirmed by the current room
// being one of the boss's rooms. This covers the common "exp + *Combat Off*"
// fallback death, which carries no candidate identity of its own; a specific
// death-line candidate name is accepted as a secondary match. Deaths we can't
// attribute (no engaged name, no candidate) fall to the tab's manual override.
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

    // Stamp a kill at now (UTC) and persist. Used by auto-detection.
    public void MarkKilled(string name) => MarkKilled(name, DateTimeOffset.UtcNow);

    // Stamp a kill at a specific time and persist. Used by the tab's manual "Mark"
    // dialog, where the user can back-date the kill.
    public void MarkKilled(string name, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _killed[name] = at.ToUniversalTime();
        Persist();
        _log?.Info("Bosses", $"timer set for '{name}' at {at.ToUniversalTime():u}");
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
        if (BossCatalog.EffectiveRegenHours(_gameData, def) is not { } hours || hours <= 0) return null;
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

    // Auto-start on a boss death. Attribution is by NAME: the monster we were
    // engaged with (engagedName = CombatManager.CurrentTarget, read live — this is
    // what a fallback exp+Off death is, a death of whatever we were fighting), or a
    // specific death-line candidate name, matched against a boss whose rooms include
    // the current room. key/engagedName are read live at the death (the event itself
    // carries neither location nor the engaged name).
    public void OnMonsterDied(MonsterDeathEvent evt, RoomKey? key, string? engagedName)
    {
        if (key is not { } here) return;   // can't confirm placement

        foreach (BossDef def in _bosses.Resolve())
        {
            if (def.RespawnType != BossRespawnType.Timed) continue;
            if (!RoomsContain(def, here)) continue;
            if (NameMatches(def.Name, engagedName)
                || evt.Candidates.Any(c => NameMatches(def.Name, c.Name)))
            {
                MarkKilled(def.Name);
                return;
            }
        }
    }

    private static bool RoomsContain(BossDef def, RoomKey key)
    {
        foreach (string wire in def.Rooms)
            if (RoomKey.TryParseWire(wire, out RoomKey k) && k == key) return true;
        return false;
    }

    // Case-insensitive name match tolerant of a leading article ("the ogre king"
    // matches boss "ogre king"). The observed name (engaged target / death line)
    // must contain the canonical boss name.
    private static bool NameMatches(string bossName, string? observed)
    {
        if (string.IsNullOrWhiteSpace(observed)) return false;
        string boss = StripArticle(bossName);
        return boss.Length > 0 && StripArticle(observed).Contains(boss, StringComparison.Ordinal);
    }

    private static string StripArticle(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s.StartsWith("the ", StringComparison.Ordinal)) return s[4..];
        if (s.StartsWith("an ", StringComparison.Ordinal)) return s[3..];
        if (s.StartsWith("a ", StringComparison.Ordinal)) return s[2..];
        return s;
    }

    private void Persist()
    {
        if (ActiveSet is null) return;
        JsonStore.Save(AppPaths.BossTimersFile(ActiveSet), _killed);
    }
}
