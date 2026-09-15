using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// Owns the login-time quest-flag sync: read the character's live flag values (via
// QuestFlagProbe, realm-aware), compare each crawled quest's effective complete value
// against them, and mark the newly-complete ones on the profile's QuestLog. Strictly
// one-way — it only ever sets Complete, never clears — so a mis-read or a mis-derived
// complete value can at worst miss a completion the user ticks by hand.
//
// The effective complete value is the per-quest editor override (QuestDefinition.
// CompleteValueOverride) when set, else the crawl's derived CrawledQuest.CompleteValue.
// Writes go straight to CharacterProfile.QuestLog + ProfileService.Save so the sync works
// whether or not the Quest tab is open; Synced fires afterward so an open Quest tab reloads
// (and the VM-independent bonus consumers recompute from the log on their next open).
public sealed class QuestFlagSyncManager
{
    private readonly GameDataCache _gameData;
    private readonly ProfileService _profile;
    private readonly QuestStore _quests;
    private readonly QuestFlagProbe _probe;
    private readonly Func<RealmType> _realm;
    private readonly Func<bool> _hasSysopPowers;
    private readonly Func<string?> _characterName;
    private readonly Func<int?> _classId;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _isInRealm;
    private readonly Action _reannounce;
    private readonly Func<IReadOnlyList<QuestAvailabilityInfo>> _availableQuests;
    private readonly LogService? _log;
    private bool _running;

    // Raised after a sync that marked at least one quest complete, so an open Quest tab
    // reloads its cards from the freshly-saved log.
    public event Action? Synced;

    // Human-readable result of the last sync, for the bug report. Null until one runs.
    public string? LastResult { get; private set; }

    public QuestFlagSyncManager(
        GameDataCache gameData, ProfileService profile, QuestStore quests, QuestFlagProbe probe,
        Func<RealmType> realm, Func<bool> hasSysopPowers, Func<string?> characterName,
        Func<int?> classId, Func<bool> enabled, Func<bool> isInRealm, Action reannounce,
        Func<IReadOnlyList<QuestAvailabilityInfo>> availableQuests, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(hasSysopPowers);
        ArgumentNullException.ThrowIfNull(characterName);
        ArgumentNullException.ThrowIfNull(classId);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(isInRealm);
        ArgumentNullException.ThrowIfNull(reannounce);
        ArgumentNullException.ThrowIfNull(availableQuests);
        _gameData = gameData;
        _profile = profile;
        _quests = quests;
        _probe = probe;
        _realm = realm;
        _hasSysopPowers = hasSysopPowers;
        _characterName = characterName;
        _classId = classId;
        _enabled = enabled;
        _isInRealm = isInRealm;
        _reannounce = reannounce;
        _availableQuests = availableQuests;
        _log = log;
    }

    // Whether the active character has opted into the login sync (Settings → General).
    public bool EnabledForCurrentProfile => _profile.Current is not null && _enabled();

    // The local date this character last ran a real flag check — for the bug report.
    public DateOnly? LastSyncDate => _profile.Current?.LastQuestFlagSyncDate;

    // Once-per-day gate: true when a real check already completed on `today`, so a
    // relog the same day skips the abil / sys-god burst. Pure + static for the test.
    public static bool AlreadyCheckedToday(DateOnly? lastSync, DateOnly today) =>
        lastSync == today;

    // Stamp today's date and persist, so the rest of the day's logins skip the sync.
    private void StampCheckedToday(CharacterProfile prof)
    {
        prof.LastQuestFlagSyncDate = DateOnly.FromDateTime(DateTime.Now);
        _profile.Save();
    }

    // On-demand run — for turning the setting on mid-session, when we're already playing.
    // Gated on enabled + in-realm (so flipping it at the character-select menu does nothing),
    // it syncs then RE-reports the now-current available quests (freshly-completed ones drop
    // off). Guarded against overlap; swallows its own errors since callers fire and forget.
    public async Task RunNowAsync(CancellationToken ct = default)
    {
        if (_running || !EnabledForCurrentProfile || !_isInRealm()) return;
        _running = true;
        try
        {
            await SyncAsync(ct).ConfigureAwait(true);
            _reannounce();
        }
        catch (Exception ex) { _log?.Warn("QuestFlags", $"on-demand sync failed: {ex.Message}"); }
        finally { _running = false; }
    }

    // Read the flags, mark newly-complete quests, persist. Returns the number marked. Must
    // be awaited on the UI thread (the probe collects on the line-emit thread and paces its
    // sends off the dispatcher).
    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        if (_profile.Current is not { } prof) return 0;

        // Once per day: a relog later the same day doesn't re-fire the abil / sys-god
        // burst. Stamped only when a real check actually completes below (not on the
        // missing-sys-powers skip), so granting powers later the same day can still run.
        if (AlreadyCheckedToday(prof.LastQuestFlagSyncDate, DateOnly.FromDateTime(DateTime.Now)))
        {
            LastResult = "skipped — already synced today";
            _log?.Info("QuestFlags", $"sync: {LastResult}");
            return 0;
        }

        int? classId = _classId();
        IReadOnlyList<CrawledQuest> crawled = QuestCrawler.Crawl(_gameData, classId);
        if (crawled.Count == 0) return 0;

        // Only quests this character can actually complete at its current level — the same
        // eligible + level-met + incomplete set the availability announce uses. On paradigm
        // this bounds the `abil` burst to relevant flags (not every quest in the realm); on
        // both realms it keeps the marking to quests the character could really have done.
        var eligible = new HashSet<(int Flag, int Step)>(
            _availableQuests().Select(q => (q.Flag, q.Step)));

        List<QuestFlagCompletion.Target> targets = crawled
            .Where(q => eligible.Contains((q.Flag, q.Step)))
            .Select(q => new QuestFlagCompletion.Target(
                q.Flag, q.Step, _quests.Resolve(q.Flag, q.Step).CompleteValueOverride ?? q.CompleteValue))
            .ToList();
        if (targets.Count == 0)
        {
            LastResult = "nothing to check (no eligible incomplete quests at this level)";
            _log?.Info("QuestFlags", $"sync: {LastResult}");
            StampCheckedToday(prof);
            return 0;
        }

        HashSet<QuestFlagCompletion.QuestKey> alreadyComplete = BuildAlreadyComplete(prof.QuestLog);
        IReadOnlyList<int> flags = QuestFlagCompletion.FlagsToQuery(targets, alreadyComplete);
        if (flags.Count == 0)
        {
            LastResult = "nothing to check (no incomplete detectable quests)";
            _log?.Info("QuestFlags", $"sync: {LastResult}");
            StampCheckedToday(prof);
            return 0;
        }

        IReadOnlyDictionary<int, int> observed;
        if (_realm() == RealmType.ParaMud)
        {
            observed = await _probe.ReadParadigmAsync(flags, ct).ConfigureAwait(true);
        }
        else if (_hasSysopPowers())
        {
            observed = await _probe.ReadStockAsync(_characterName() ?? string.Empty, ct).ConfigureAwait(true);
        }
        else
        {
            LastResult = "skipped — stock flag read needs sys-god powers for this BBS";
            _log?.Info("QuestFlags", $"sync {LastResult}");
            return 0;
        }

        IReadOnlyList<QuestFlagCompletion.QuestKey> newly =
            QuestFlagCompletion.ResolveNewlyComplete(targets, observed, alreadyComplete);
        if (newly.Count == 0)
        {
            LastResult = $"{observed.Count} flag(s) read, nothing newly complete";
            _log?.Info("QuestFlags", $"sync: {LastResult}");
            StampCheckedToday(prof);
            return 0;
        }

        ApplyComplete(prof, newly);
        prof.LastQuestFlagSyncDate = DateOnly.FromDateTime(DateTime.Now);
        _profile.Save();
        LastResult = $"{observed.Count} flag(s) read, {newly.Count} quest(s) marked complete";
        _log?.Info("QuestFlags",
            $"sync marked {newly.Count} quest(s) complete: {string.Join(", ", newly.Select(k => $"{k.Flag}/{k.Step}"))}");
        Synced?.Invoke();
        return newly.Count;
    }

    private static HashSet<QuestFlagCompletion.QuestKey> BuildAlreadyComplete(IReadOnlyList<QuestProgress>? log)
    {
        var set = new HashSet<QuestFlagCompletion.QuestKey>();
        if (log is null) return set;
        foreach (QuestProgress p in log)
            if (p.Complete) set.Add(new QuestFlagCompletion.QuestKey(p.Flag, p.Step));
        return set;
    }

    private static void ApplyComplete(CharacterProfile prof, IReadOnlyList<QuestFlagCompletion.QuestKey> keys)
    {
        List<QuestProgress> log = prof.QuestLog is { } existing
            ? new List<QuestProgress>(existing)
            : new List<QuestProgress>();
        var byKey = log.ToDictionary(p => (p.Flag, p.Step));
        foreach (QuestFlagCompletion.QuestKey k in keys)
        {
            if (byKey.TryGetValue((k.Flag, k.Step), out QuestProgress? p)) p.Complete = true;
            else
            {
                var np = new QuestProgress(k.Flag, k.Step) { Complete = true };
                log.Add(np);
                byKey[(k.Flag, k.Step)] = np;
            }
        }
        prof.QuestLog = log;
    }
}
