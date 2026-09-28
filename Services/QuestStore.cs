using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Loads and resolves quest definitions. Two layers merge per (flag, step) in
// priority order:
//   1. the user's overlay, the active realm's quests.json — display name,
//      show/hide visibility, edited step markdown. A player's edits belong to the
//      realm they play, so the overlay follows the active realm (it wins over the
//      seed), reloading on ProfileService.ProfileLoaded / BbsPinApplied /
//      ProfileClosed;
//   2. the universal read-only seed QuestDefs.seed.json in Data/Global, built
//      from the bundled Defaults seed, keyed by game-data flag numbers (custom
//      realms reuse the numbers) so a curated default ports across every board;
//   3. an auto-draft (blank name, shown, no edited steps) for any quest the
//      crawler discovers that neither layer names yet.
// The seed is never written. The mechanical data — ordered steps + stat bonuses —
// is crawled from the active set's TBInfo elsewhere; this store owns only the
// user/seed text layer.
//
// The overlay also holds two user-driven extras: manual quests the crawler
// never finds (identity in the reserved QuestDefinition.ManualFlagBase flag
// range, persisted verbatim since they have no crawl baseline — see
// ManualQuests), and a QuestDefinition.Blocked flag that suppresses a
// spuriously-crawled quest from the journal in sets where it shouldn't appear.
public sealed class QuestStore
{
    private readonly LogService? _log;
    private readonly string _seedPath;
    private readonly Func<string?>? _activeRealmFolder;
    private readonly Dictionary<(int Flag, int Step), QuestDefinition> _seed = new();
    private readonly Dictionary<(int Flag, int Step), QuestDefinition> _overlay = new();

    // Folder of the realm whose overlay is loaded, or null when none.
    public string? ActiveRealmFolder { get; private set; }

    // Raised after the overlay reloads (realm change / profile close) so consumers
    // re-resolve their displayed quest text.
    public event Action? Reloaded;

    // Production ctor: seed from Data/Global; the overlay tracks the active realm,
    // reloading on ProfileService.ProfileLoaded / BbsPinApplied / ProfileClosed.
    // profile + activeRealmFolder are parameterized so tests can drive the store
    // without a live ProfileService (pass a null provider and call
    // OnActiveRealmChanged directly). seedPath defaults to
    // AppPaths.DefaultQuestDefsSeedFile so a test can point at a scratch seed.
    public QuestStore(ProfileService? profile = null, Func<string?>? activeRealmFolder = null,
                      LogService? log = null, string? seedPath = null)
    {
        _log = log;
        _activeRealmFolder = activeRealmFolder;
        _seedPath = seedPath ?? AppPaths.DefaultQuestDefsSeedFile;
        LoadInto(_seed, _seedPath, "seed");

        if (profile is not null)
        {
            // ProfileLoaded too: a profile swap otherwise kept the realm resolved
            // mid-swap (while no profile was current), which could be another board's.
            profile.ProfileLoaded += _ => ReloadForActiveRealm();
            profile.BbsPinApplied += _ => ReloadForActiveRealm();
            profile.ProfileClosed += ReloadForActiveRealm;
        }
        ReloadForActiveRealm();
    }

    // Reload the overlay for whatever realm the provider reports as active.
    private void ReloadForActiveRealm() => OnActiveRealmChanged(_activeRealmFolder?.Invoke());

    // Swap the loaded overlay to the realm folder's quests.json (empty when the
    // realm has no overlay yet, or the folder is blank). Public so tests can drive
    // it directly; production reloads via the profile hooks.
    public void OnActiveRealmChanged(string? realmFolder)
    {
        _overlay.Clear();
        ActiveRealmFolder = string.IsNullOrWhiteSpace(realmFolder) ? null : realmFolder;
        if (ActiveRealmFolder is not null)
            LoadInto(_overlay, AppPaths.RealmQuestsFile(ActiveRealmFolder), "overlay");
        Reloaded?.Invoke();
    }

    // Resolve the effective definition for a quest: the user overlay if it names
    // this (flag, step); else the universal seed; else a blank-named, visible
    // auto-draft. Never returns null.
    public QuestDefinition Resolve(int flag, int step)
    {
        (int Flag, int Step) key = (flag, step);
        if (_overlay.TryGetValue(key, out QuestDefinition? user)) return user;
        if (_seed.TryGetValue(key, out QuestDefinition? seeded)) return seeded;
        return new QuestDefinition(flag, step);
    }

    // Persist the user's edited definitions to the active realm's overlay
    // (its quests.json) and refresh the in-memory layer so later
    // Resolve calls see the edits immediately. The overlay stays a delta: a
    // definition that matches what Resolve would return with no overlay (the seed
    // entry, or a blank auto-draft) is dropped rather than frozen into the file,
    // so a later seed update still flows through for untouched quests. No-op when
    // no realm is active.
    public void Save(IEnumerable<QuestDefinition> defs)
    {
        ArgumentNullException.ThrowIfNull(defs);
        if (ActiveRealmFolder is null) return;

        _overlay.Clear();
        foreach (QuestDefinition raw in defs)
        {
            QuestDefinition def = Normalize(raw);
            if (QuestDefinition.IsManual(def.Flag))
            {
                // A manual quest has no crawl/seed baseline to regenerate it, so it persists
                // verbatim rather than as a delta — except a wholly-blank "Add Quest" row the
                // user never filled in, which is dropped so it doesn't clutter the overlay.
                if (IsEmptyManual(def)) continue;
            }
            else if (SameContent(def, Baseline(def.Flag, def.Step))) continue; // delta-only
            _overlay[(def.Flag, def.Step)] = def;
        }

        List<QuestDefinition> list = _overlay.Values
            .OrderBy(d => d.Flag).ThenBy(d => d.Step)
            .ToList();
        try
        {
            JsonStore.Save(AppPaths.RealmQuestsFile(ActiveRealmFolder), list);
        }
        catch (Exception ex)
        {
            // A failed write (permissions, disk) shouldn't crash the editor — the
            // in-memory overlay still reflects the edits for this session.
            _log?.Warn("Quests", $"Failed to save overlay to '{ActiveRealmFolder}': {ex.Message}");
        }
    }

    // Every user-added (manual) quest the store knows for the active set,
    // resolved (overlay over seed) and ordered by flag. These carry no crawl
    // backing, so the Quest Status tab and editor materialize them straight from
    // the definition.
    public IReadOnlyList<QuestDefinition> ManualQuests()
    {
        var keys = new HashSet<(int Flag, int Step)>();
        foreach ((int Flag, int Step) k in _seed.Keys) if (QuestDefinition.IsManual(k.Flag)) keys.Add(k);
        foreach ((int Flag, int Step) k in _overlay.Keys) if (QuestDefinition.IsManual(k.Flag)) keys.Add(k);
        return keys
            .OrderBy(k => k.Flag).ThenBy(k => k.Step)
            .Select(k => Resolve(k.Flag, k.Step))
            .ToList();
    }

    // Ordered distinct step (band) values known for a flag across seed + overlay.
    // Drives the ordinal band numbering in the @quest report — the Nth step in this
    // list is band N. Empty when the flag is unknown to both layers.
    public IReadOnlyList<int> StepsForFlag(int flag)
    {
        SortedSet<int> steps = new();
        foreach ((int Flag, int Step) k in _seed.Keys) if (k.Flag == flag) steps.Add(k.Step);
        foreach ((int Flag, int Step) k in _overlay.Keys) if (k.Flag == flag) steps.Add(k.Step);
        return steps.ToList();
    }

    // Every named quest known across seed + overlay as (flag, resolved name) with a
    // non-empty name — the candidate set the @quest name resolver matches against.
    public IEnumerable<(int Flag, string Name)> NamedQuests()
    {
        HashSet<(int Flag, int Step)> keys = new(_seed.Keys);
        foreach ((int Flag, int Step) k in _overlay.Keys) keys.Add(k);
        foreach ((int Flag, int Step) k in keys)
        {
            string name = Resolve(k.Flag, k.Step).Name;
            if (!string.IsNullOrWhiteSpace(name)) yield return (k.Flag, name);
        }
    }

    // The no-overlay resolution for a quest: the seed entry if one exists, else a
    // blank auto-draft. Save compares each edited def against this to decide whether
    // the def is a genuine user delta worth writing.
    private QuestDefinition Baseline(int flag, int step) =>
        _seed.TryGetValue((flag, step), out QuestDefinition? seeded)
            ? Normalize(seeded)
            : new QuestDefinition(flag, step);

    private static QuestDefinition Normalize(QuestDefinition d) =>
        new(d.Flag, d.Step,
            (d.Name ?? string.Empty).Trim(),
            d.Visible,
            string.IsNullOrWhiteSpace(d.Steps) ? null : d.Steps,
            string.IsNullOrWhiteSpace(d.Rewards) ? null : d.Rewards,
            d.RequiredLevel,
            d.Blocked,
            d.CompleteValueOverride)
        { ClassRestrict = d.ClassRestrict is { Count: > 0 } ? d.ClassRestrict : null };

    // A manual row the user added but left wholly blank — nothing worth persisting.
    private static bool IsEmptyManual(QuestDefinition d) =>
        string.IsNullOrWhiteSpace(d.Name) && d.Steps is null && d.Rewards is null
        && d.RequiredLevel is null && !d.Blocked && d.CompleteValueOverride is null
        && (d.ClassRestrict is null || d.ClassRestrict.Count == 0);

    private static bool SameContent(QuestDefinition a, QuestDefinition b) =>
        string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && a.Visible == b.Visible
        && string.Equals(a.Steps, b.Steps, StringComparison.Ordinal)
        && string.Equals(a.Rewards, b.Rewards, StringComparison.Ordinal)
        && a.RequiredLevel == b.RequiredLevel
        && a.Blocked == b.Blocked
        && a.CompleteValueOverride == b.CompleteValueOverride
        && SameClassRestrict(a.ClassRestrict, b.ClassRestrict);

    // Order-insensitive equality of two class-restriction lists, treating null and
    // empty alike (neither is a restriction).
    private static bool SameClassRestrict(List<int>? a, List<int>? b)
    {
        int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
        if (ca == 0) return cb == 0;
        return ca == cb && a!.OrderBy(x => x).SequenceEqual(b!.OrderBy(x => x));
    }

    private void LoadInto(Dictionary<(int Flag, int Step), QuestDefinition> target, string path, string label)
    {
        target.Clear();
        List<QuestDefinition>? defs;
        try
        {
            defs = JsonStore.Load<List<QuestDefinition>>(path);
        }
        catch (Exception ex)
        {
            // A hand-edited overlay/seed with malformed JSON shouldn't crash the
            // workshop — log and fall through to an empty layer.
            _log?.Warn("Quests", $"Failed to load {label} '{path}': {ex.Message}");
            return;
        }
        if (defs is null) return;
        foreach (QuestDefinition def in defs)
            target[(def.Flag, def.Step)] = def;
    }
}
