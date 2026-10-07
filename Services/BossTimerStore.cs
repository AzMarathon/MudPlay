using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Persisted per-realm boss kill-times. On a confirmed boss kill (a specific
// MonsterDied identity that matches a tracked boss AND lands in one of that boss's
// rooms) the kill time is stamped and written to the realm's boss-timers.json, so
// a long respawn timer survives an app restart. Timer VALUES aren't stored — the
// full respawn hours are resolved live from game data (BossCatalog), and the
// realm's early-window model comes from BossTimerMath. Kill-times are observed in
// play, so they belong to the realm (two realms on the same game data don't share
// them); every character on the realm shares them.
//
// Those characters are often online together, one client each, and each holds its
// own copy of the timers. So the file is re-read whenever another client wrote it,
// on a poll and before every change, and a change is then written on top of what
// that client last saved. Written from its own copy alone, the file lost every kill
// the other client had made: after a restart one character's timers had been
// replaced by the other's.
//
// Kill detection matches the in-game signal: we were ENGAGED with a monster by
// name, then it died (awarded exp). Monster numbers aren't observable in-game, so
// attribution is by NAME — the engaged target name (CombatManager.CurrentTarget,
// read live at death) matched against the boss list, confirmed by the current room
// being one of the boss's rooms. Two bosses the game gives one name (BossDef.GameName)
// are told apart by that room. This covers the common "exp + *Combat Off*"
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

    // Fallback kill detection (roster disappearance). The boss-table monsters
    // currently seen present in the room roster, and the room that set applies
    // to. A boss that WAS present and then vanishes from a full "Also here:"
    // re-parse (AlsoHere) — with no departure line to explain it — is a kill,
    // even when we never engaged it (a party member's kill, or a witnessed
    // death). Departures / room-changes update the set WITHOUT marking, so a mob
    // that merely walked away can't be mistaken for a kill.
    private readonly HashSet<string> _bossesPresent = new(StringComparer.OrdinalIgnoreCase);
    private RoomKey? _presentRoom;

    // "Also here:" comes before "Obvious exits:", so when the roster is parsed the
    // tracker still holds the room we were in, whether the display is a re-show of it
    // or the room a move just took us into. A boss missing from it is only a kill once
    // that display's name proves it's the same room (report paradigm-20260928-163621:
    // `go manhole` out of Town Square read the tunnel's roster as Town Square without
    // Mayor Godfrey and started his timer).
    private (RoomKey Room, string RoomName, List<string> Names)? _pendingVanish;

    // A fallback vanish this close behind a primary-path (engaged-then-exp) mark
    // is the SAME kill seen twice — skip it so the timer isn't re-stamped and
    // Grab-All isn't double-fired.
    private static readonly TimeSpan FallbackDedupeWindow = TimeSpan.FromSeconds(6);

    // Active realm's nightly-cleanup config (time-of-day + zone) for "Respawns @
    // Cleanup" bosses. Resolved live so a realm / setting change takes effect without
    // re-wiring; null when unset or unparseable → cleanup bosses can't auto-flip.
    private Func<BossCleanupConfig?>? _cleanupConfig;

    // Folder of the realm whose kill-times are loaded — timers are observed in play,
    // so they belong to the realm. null disables persistence.
    public string? ActiveRealmFolder { get; private set; }

    public void SetCleanupConfig(Func<BossCleanupConfig?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _cleanupConfig = resolver;
    }

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

    public void OnRealmChanged(string? realmFolder)
    {
        ActiveRealmFolder = string.IsNullOrWhiteSpace(realmFolder) ? null : realmFolder;
        Load();
        Changed?.Invoke();
    }

    // Write time of the file as this client last read or wrote it.
    private DateTime _seenWrite = DateTime.MinValue;

    private void Load()
    {
        _killed = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        _seenWrite = DateTime.MinValue;
        if (ActiveRealmFolder is null) return;
        string path = AppPaths.RealmBossTimersFile(ActiveRealmFolder);
        _seenWrite = WriteTime(path);
        if (JsonStore.Load<Dictionary<string, DateTimeOffset>>(path) is { } loaded)
            foreach ((string name, DateTimeOffset at) in loaded) _killed[name] = at;
    }

    // Re-read the timers when another client on the realm has written them. Called on
    // the heartbeat, so a kill made on one client shows on the others within a
    // moment. True when anything was re-read.
    public bool TakeInOutsideChanges()
    {
        if (ActiveRealmFolder is null) return false;
        if (WriteTime(AppPaths.RealmBossTimersFile(ActiveRealmFolder)) == _seenWrite) return false;
        try { Load(); }
        catch (Exception ex)
        {
            // Caught mid-write by the other client: try again on the next poll.
            _log?.Debug("Bosses", $"boss timers not re-read yet ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
        _log?.Debug("Bosses", "boss timers re-read: another client on this realm changed them");
        Changed?.Invoke();
        return true;
    }

    private static DateTime WriteTime(string path) =>
        File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    public DateTimeOffset? KilledAt(string name)
        => _killed.TryGetValue(name, out DateTimeOffset at) ? at : null;

    // Stamp a kill at now (UTC) and persist. Used by auto-detection.
    public void MarkKilled(string name) => MarkKilled(name, DateTimeOffset.UtcNow);

    // Stamp a kill at a specific time and persist. Used by the tab's manual "Mark"
    // dialog, where the user can back-date the kill.
    public void MarkKilled(string name, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        TakeInOutsideChanges();
        _killed[name] = at.ToUniversalTime();
        Persist();
        _log?.Info("Bosses", $"timer set for '{name}' at {at.ToUniversalTime():u}");
        Changed?.Invoke();
    }

    // Clear a boss's timer (manual reset / mistaken start).
    public void Reset(string name)
    {
        TakeInOutsideChanges();
        if (_killed.Remove(name))
        {
            Persist();
            _log?.Info("Bosses", $"timer cleared for '{name}'");
            Changed?.Invoke();
        }
    }

    // Live window state for a boss, or null when it has no active timer (never
    // killed, already expired / respawned, or no game-data respawn timer). A Cleanup
    // boss is "active" while it reads DEAD — its state carries the "cleanup" label
    // and the time until the next cleanup flips it back to ALIVE.
    public BossWindowState? StatusFor(BossDef def, RealmType realm)
    {
        ArgumentNullException.ThrowIfNull(def);
        if (KilledAt(def.Name) is not { } killed) return null;

        if (def.RespawnType == BossRespawnType.Cleanup)
        {
            if (CleanupRemainingFrom(killed) is not { } rem) return null;   // alive → not tracked
            return new BossWindowState(false, rem, "cleanup", rem);
        }

        if (BossCatalog.EffectiveRegenHours(_gameData, def) is not { } hours || hours <= 0) return null;
        BossWindowState state = BossTimerMath.Describe(realm, hours, DateTimeOffset.UtcNow - killed);
        return state.Expired ? null : state;
    }

    // Every un-passed early spawn window for a percentage boss (Paradigm -20/-10/-5, Stock
    // 87.5%), soonest first, for the @timer report's full window list. Empty for a cleanup
    // boss, an untimed boss, or one past every early point — same guards as StatusFor.
    public IReadOnlyList<(string Label, TimeSpan Remaining)> EarlyWindowsFor(BossDef def, RealmType realm)
    {
        ArgumentNullException.ThrowIfNull(def);
        if (def.RespawnType == BossRespawnType.Cleanup) return Array.Empty<(string, TimeSpan)>();
        if (KilledAt(def.Name) is not { } killed) return Array.Empty<(string, TimeSpan)>();
        if (BossCatalog.EffectiveRegenHours(_gameData, def) is not { } hours || hours <= 0)
            return Array.Empty<(string, TimeSpan)>();
        return BossTimerMath.EarlyWindows(realm, hours, DateTimeOffset.UtcNow - killed);
    }

    // A Cleanup boss reads DEAD once marked, until the next cleanup. True only when
    // marked and the cleanup that clears it hasn't arrived (or can't be computed —
    // no cleanup time set keeps it DEAD until manually cleared).
    public bool IsCleanupDead(string name)
    {
        if (KilledAt(name) is not { } killed) return false;
        if (_cleanupConfig?.Invoke() is not { } cfg) return true;   // marked, no cleanup time → stays dead
        return DateTimeOffset.UtcNow < BossTimerMath.NextCleanup(killed, cfg.TimeOfDay, cfg.Tz);
    }

    // Time until a marked Cleanup boss flips back to ALIVE, or null when it isn't
    // marked, the cleanup has already passed, or no cleanup time is configured.
    public TimeSpan? CleanupRemaining(string name)
        => KilledAt(name) is { } killed ? CleanupRemainingFrom(killed) : null;

    // When a marked Cleanup boss flips back to ALIVE — the nightly cleanup after its
    // kill — or null when it isn't marked or no cleanup time is configured.
    public DateTimeOffset? NextCleanupFor(string name)
        => KilledAt(name) is { } killed && _cleanupConfig?.Invoke() is { } cfg
            ? BossTimerMath.NextCleanup(killed, cfg.TimeOfDay, cfg.Tz)
            : null;

    private TimeSpan? CleanupRemainingFrom(DateTimeOffset killed)
    {
        if (_cleanupConfig?.Invoke() is not { } cfg) return null;
        TimeSpan rem = BossTimerMath.NextCleanup(killed, cfg.TimeOfDay, cfg.Tz) - DateTimeOffset.UtcNow;
        return rem > TimeSpan.Zero ? rem : null;
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
    // Fires when a death is attributed to a tracked boss in the current room — the
    // matched BossDef (whichever respawn type). Fires for EVERY matched boss. Kept
    // separate from the timer marking so a cleanup boss (no timer) still notifies.
    public event Action<BossDef>? BossKilled;

    // Fires for every death that leaves a boss's loot on the floor: the boss's own
    // (alongside BossKilled), and the death of a monster the boss's death summoned
    // (Lord Chisholm's malformation, the mayor of Arlysia's arachnigoth), which is
    // not a kill of the boss and starts no timer. Carries what was seen of the dead
    // monster, its name and the exp it paid, either of them null when unseen, for
    // Grab All to read the right record's drops.
    public event Action<BossDef, string?, int?>? BossLootDropped;

    // The names of the monsters a boss's death summons, down the whole chain.
    private Func<BossDef, IReadOnlyList<string>>? _deathSummons;
    public void SetDeathSummonResolver(Func<BossDef, IReadOnlyList<string>> deathSummons)
    {
        ArgumentNullException.ThrowIfNull(deathSummons);
        _deathSummons = deathSummons;
    }

    //
    // recentFoes are the monsters named by the fight's damage lines. They matter only
    // when nothing else names the dead monster: no engaged target (a room too dark to
    // list anyone) and no death line on record. A boss of this room those lines named
    // is known to be here, and the unnamed death is its own when (user, 2026-10-07):
    //   - the exp gained is a share the boss could have paid and no other monster of
    //     the room could have (UnnamedDeathIsTheBosss); or
    //   - exp can't tell them apart, and the boss is the only monster the lines named.
    public void OnMonsterDied(MonsterDeathEvent evt, RoomKey? key, string? engagedName,
        IReadOnlyCollection<string>? recentFoes = null)
    {
        if (key is not { } here) return;   // can't confirm placement

        bool unnamed = string.IsNullOrWhiteSpace(engagedName) && evt.Candidates.Count == 0;
        foreach (BossDef def in _bosses.Current)
        {
            if (!RoomsContain(def, here)) continue;
            bool named = NameMatches(def.MatchName, engagedName)
                         || evt.Candidates.Any(c => NameMatches(def.MatchName, c.Name));
            if (!named)
            {
                if (!unnamed || recentFoes is not { Count: > 0 }
                    || !recentFoes.Any(n => NameMatches(def.MatchName, n)))
                    continue;
                if (!UnnamedDeathIsTheBosss(def, here, evt.ExperienceGained, recentFoes, out string why)) continue;
                _log?.Info("Bosses", $"boss '{def.Name}' was in the fight's damage lines and {why} — taken as the kill");
            }

            // A timed boss starts its respawn countdown; a cleanup boss has none. Both
            // notify BossKilled so Grab-All can fire regardless of respawn type.
            if (def.RespawnType == BossRespawnType.Timed) MarkKilled(def.Name);
            BossKilled?.Invoke(def);
            // The name that matched is the dead monster's; an unnamed death has none.
            string? sawDie = !named ? null
                : NameMatches(def.MatchName, engagedName) ? engagedName
                : evt.Candidates.First(c => NameMatches(def.MatchName, c.Name)).Name;
            BossLootDropped?.Invoke(def, sawDie, evt.ExperienceGained);
            return;
        }

        // Not a boss of this room by name: it may be what one of them turned into.
        if (unnamed || _deathSummons is null) return;
        foreach (BossDef def in _bosses.Current)
        {
            if (!RoomsContain(def, here)) continue;
            foreach (string summoned in _deathSummons(def))
            {
                string? sawDie = NameMatches(summoned, engagedName) ? engagedName
                    : evt.Candidates.Where(c => NameMatches(summoned, c.Name)).Select(c => c.Name).FirstOrDefault();
                if (sawDie is null) continue;
                _log?.Info("Bosses", $"'{sawDie}' died in {here}: what the death of boss '{def.Name}' summons");
                BossLootDropped?.Invoke(def, sawDie, evt.ExperienceGained);
                return;
            }
        }
    }

    // What the boss is worth, and what each other monster that can be in the room is
    // worth (its lair and placed monsters), for telling an unnamed death apart by the
    // exp it paid. Unset, or nothing on record for the boss, leaves only the "nothing
    // else was named" test.
    private Func<BossDef, RoomKey, (long BossExp, IReadOnlyList<long> OtherExp)>? _roomExp;
    public void SetRoomExpResolver(Func<BossDef, RoomKey, (long BossExp, IReadOnlyList<long> OtherExp)> roomExp)
    {
        ArgumentNullException.ThrowIfNull(roomExp);
        _roomExp = roomExp;
    }

    private static bool CouldPay(long worth, long gained) => Game.Inventory.BossDeathLoot.CouldPay(worth, gained);

    // The unnamed death is the boss's when the exp gained is something the boss could
    // have paid and nothing else in the room could have (user, 2026-10-07): a gain
    // inside the boss's range and outside every other monster's. That holds whether
    // the boss is worth far more than its adds (the Darken Beast Lord, 4,000,000
    // against 17,750) or far less than its neighbours (the monkey spirit, 40,000
    // among monsters worth 400,000). Where the ranges overlap and the gain lands in
    // the overlap, or the boss has no exp on record, exp can't say: then it is the
    // boss's only when the boss was the only monster the lines named.
    private bool UnnamedDeathIsTheBosss(BossDef def, RoomKey here, int? expGained,
        IReadOnlyCollection<string> recentFoes, out string why)
    {
        (long bossExp, IReadOnlyList<long> others) = _roomExp?.Invoke(def, here) ?? (0, Array.Empty<long>());
        if (expGained is { } gained && CouldPay(bossExp, gained) && !others.Any(o => CouldPay(o, gained)))
        {
            why = $"the {gained:N0} exp gained is what it pays ({bossExp:N0}, shared) and nothing else here could";
            return true;
        }
        why = "nothing else was named";
        return recentFoes.All(n => NameMatches(def.MatchName, n));
    }

    // Fallback kill signal: a boss-table monster we saw in the room roster is
    // gone from a full "Also here:" re-parse of the SAME room. Attributes a death
    // we never engaged (a party member's kill, a witnessed one) that the
    // exp-inferred MonsterDied path can't name. Only an AlsoHere re-parse marks a
    // kill — a Departure / RoomChange updates the present-set without marking, so
    // a mob that walked away is never mistaken for a kill.
    // roomName is the tracker's name for here; movePending says a move is in flight,
    // so the roster may already be the next room's.
    public void OnRoomEntitiesObserved(
        RoomEntitiesObservation obs, RoomKey? here, string? roomName = null, bool movePending = false)
    {
        _pendingVanish = null;
        if (here is not { } room)
        {
            _bossesPresent.Clear();
            _presentRoom = null;
            return;
        }

        HashSet<string> presentNow = BossesPresent(obs, room);

        // Moved rooms (or first observation here): re-baseline, never mark.
        if (_presentRoom != room)
        {
            _presentRoom = room;
            _bossesPresent.Clear();
            _bossesPresent.UnionWith(presentNow);
            return;
        }

        if (obs.Source == RoomObservationSource.AlsoHere && !movePending && !string.IsNullOrEmpty(roomName))
        {
            List<string> vanished = _bossesPresent.Where(n => !presentNow.Contains(n)).ToList();
            if (vanished.Count > 0) _pendingVanish = (room, roomName, vanished);
        }

        // Death / Departure / Arrival / RoomChange all just re-baseline the set:
        // a Death is handled by the engaged / candidate path, a Departure isn't a
        // kill, and an Arrival adds. AlsoHere re-baselines after marking above.
        _bossesPresent.Clear();
        _bossesPresent.UnionWith(presentNow);
    }

    // The room display that the last "Also here:" belonged to. A boss missing from that
    // roster is a kill only when this is the same room — a vanish with no departure to
    // explain it. Any other room means the roster was the next room's.
    public void OnRoomDisplayed(string roomName)
    {
        if (_pendingVanish is not { } pending) return;
        _pendingVanish = null;
        if (!string.Equals(roomName.Trim(), pending.RoomName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _log?.Info("Bosses",
                $"'{string.Join("', '", pending.Names)}' not in the roster shown in '{roomName}' — "
                + $"that's another room than {pending.Room}, not a kill");
            return;
        }
        foreach (string name in pending.Names)
        {
            // Dedupe against a just-landed primary-path mark.
            if (KilledAt(name) is { } at
                && DateTimeOffset.UtcNow - at.ToUniversalTime() < FallbackDedupeWindow)
                continue;
            _log?.Info("Bosses",
                $"boss '{name}' vanished from a re-parse of {pending.Room} — marking killed (roster fallback)");
            MarkKilled(name);
            foreach (BossDef def in _bosses.Current)
                if (string.Equals(def.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    BossKilled?.Invoke(def);
                    BossLootDropped?.Invoke(def, null, null);
                    break;
                }
        }
    }

    // The tracked-boss names present in this observation's roster whose rooms
    // include the current room — the same room + name gates OnMonsterDied uses.
    private HashSet<string> BossesPresent(RoomEntitiesObservation obs, RoomKey here)
    {
        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
        foreach (BossDef def in _bosses.Current)
        {
            if (!RoomsContain(def, here)) continue;
            foreach (RoomEntity e in obs.Entities)
            {
                if (e.Kind != EntityKind.Monster) continue;
                if (NameMatches(def.MatchName, e.ResolvedName) || NameMatches(def.MatchName, e.RawName))
                {
                    present.Add(def.Name);
                    break;
                }
            }
        }
        return present;
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
        // Lower-case AND collapse internal whitespace to a single space (Split on any
        // whitespace, drop empties, re-join). A monster name in the "Also here:" roster
        // or a death line is word-wrapped like the inventory dump, so a multi-word name
        // split across a wrap boundary comes back doubled ("colossal  midnight dragon")
        // — without collapsing, the Contains match against the single-spaced canonical
        // boss name silently misses and the kill never marks a timer. Same wrap-noise
        // the @uses item lookup had to normalise.
        s = string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (s.StartsWith("the ", StringComparison.Ordinal)) return s[4..];
        if (s.StartsWith("an ", StringComparison.Ordinal)) return s[3..];
        if (s.StartsWith("a ", StringComparison.Ordinal)) return s[2..];
        return s;
    }

    private void Persist()
    {
        if (ActiveRealmFolder is null) return;

        // Boss-timer persistence is convenience bookkeeping fired from the combat
        // death-line path — a write failure (transient IO, a filesystem hiccup)
        // must never crash the client mid-fight. Log it and carry on; the in-memory
        // timer still stands and the next kill/reset re-attempts the write.
        try
        {
            string path = AppPaths.RealmBossTimersFile(ActiveRealmFolder);
            JsonStore.Save(path, _killed);
            _seenWrite = WriteTime(path);
        }
        catch (Exception ex)
        {
            _log?.Warn("Bosses", $"failed to persist boss timers: {ex.Message}");
        }
    }
}

// Resolved nightly-cleanup config for the active BBS — the daily wall-clock time
// and the zone it's in. Built by AppServices from the BBS profile's
// CleanupTimeOfDay + CleanupTimeZoneId.
public sealed record BossCleanupConfig(TimeSpan TimeOfDay, TimeZoneInfo Tz);
