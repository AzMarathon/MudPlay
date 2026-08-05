using System;
using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Loads and resolves the boss catalog for the active game-data set. Two layers,
// keyed by boss Name (game-data spelling):
//   1. the user's per-set overlay {set}/bosses.json — added / removed bosses,
//      edited rooms, StopBefore + ExactSpawn overrides;
//   2. the universal read-only seed BossDefs.seed.json (curated from the boss-timer
//      sheet).
// Timer VALUES are not stored — resolved from game data (BossCatalog.ResolveRegenHours)
// so they track the loaded set's version. Mirrors QuestStore: the overlay is a delta
// (entries equal to the seed are dropped) and reloads on OnActiveSetChanged. The
// boss list is realm-wide (shared across the user's characters), so it keys off the
// active set, not the profile.
public sealed class BossStore
{
    private readonly LogService? _log;
    private readonly string _seedPath;
    private readonly List<BossDef> _seed = new();
    private readonly Dictionary<string, BossDef> _seedByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BossDef> _overlay = new(StringComparer.OrdinalIgnoreCase);

    // Active set whose overlay is loaded, or null when none.
    public string? ActiveSet { get; private set; }

    // seedPath defaults to the bootstrapped Global copy; parameterized so tests can
    // point at a scratch seed.
    public BossStore(LogService? log = null, string? seedPath = null)
    {
        _log = log;
        _seedPath = seedPath ?? AppPaths.DefaultBossDefsSeedFile;
        List<BossDef>? seed = JsonStore.Load<List<BossDef>>(_seedPath);
        if (seed is not null)
            foreach (BossDef b in seed) { _seed.Add(b); _seedByName[b.Name] = b; }
    }

    public void OnActiveSetChanged(string? setName)
    {
        _overlay.Clear();
        ActiveSet = string.IsNullOrWhiteSpace(setName) ? null : setName;
        if (ActiveSet is null) return;
        List<BossDef>? ov = JsonStore.Load<List<BossDef>>(AppPaths.BossesFile(ActiveSet));
        if (ov is null) return;
        foreach (BossDef b in ov) _overlay[b.Name] = b;
    }

    // The merged boss list. Overlay wins per Name; a Removed overlay entry hides the
    // seed boss; an overlay entry with no seed match is user-added. Returns clones so
    // the UI can edit rows without mutating stored instances.
    public IReadOnlyList<BossDef> Resolve()
    {
        var byName = new Dictionary<string, BossDef>(StringComparer.OrdinalIgnoreCase);
        foreach (BossDef s in _seed) byName[s.Name] = s;
        foreach ((string name, BossDef ov) in _overlay)
        {
            if (ov.Removed) byName.Remove(name);
            else byName[name] = ov;
        }
        return byName.Values.Select(b => b.Clone()).ToList();
    }

    // Bosses visible on the given realm (Stock hides Paradigm-only entries).
    public IReadOnlyList<BossDef> ResolveForRealm(RealmType realm)
        => Resolve().Where(b => realm == RealmType.ParaMud ? b.InParadigm : b.InStock).ToList();

    // Persist the user's boss list to the active set's overlay as a DELTA: a boss
    // matching the seed is dropped (so a later seed update still flows through); an
    // edited / added boss is written; a seed boss the user deleted is written as a
    // Removed tombstone. No-op when no set is active.
    public void Save(IEnumerable<BossDef> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (ActiveSet is null) return;

        var overlay = new List<BossDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BossDef b in current)
        {
            if (!seen.Add(b.Name)) continue;   // dedupe by name
            if (_seedByName.TryGetValue(b.Name, out BossDef? seed) && b.MatchesSeed(seed)) continue;
            overlay.Add(b.Clone());
        }
        foreach (BossDef s in _seed)
            if (!seen.Contains(s.Name))
                overlay.Add(new BossDef { Name = s.Name, Removed = true });

        _overlay.Clear();
        foreach (BossDef b in overlay) _overlay[b.Name] = b;
        JsonStore.Save(AppPaths.BossesFile(ActiveSet), overlay);
        _log?.Debug("Bosses", $"saved {overlay.Count} overlay delta(s) for set {ActiveSet}");
    }
}
