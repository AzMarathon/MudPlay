using System.IO;
using System.Text.Json;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// Persists a seed-backed per-set catalogue (message records, monster-name records) as
// the user's DELTA against the shipped seed instead of a full snapshot. A snapshot froze
// the catalogue at the user's first save: every later seed fix — including an in-place
// text fix that keeps a record's Id — never reached them. The per-set file now holds
// only what the user changed, and every load rebuilds the catalogue from the CURRENT
// seed.
//
// A seed record is addressed by its Id plus its Links. The Id alone isn't unique — the
// seeds reuse one Id across records whose text is identical but whose Links differ (a
// spell's priest and druid copies, one monster name on several Monsters rows) — so the
// Links break the tie. When the exact Links no longer match but the Id is unique in the
// seed, the Id alone still finds the record, so a seed fix that re-links a record
// doesn't strand the user's delta on it.
//
// Diff rules:
//   * a record matching a seed record, equal to it once the user's editable fields are
//     applied → omitted; it follows the seed.
//   * a record matching a seed record whose editable (non-identity) fields differ → an
//     Override. Only those fields are applied onto the CURRENT seed record on load, so
//     the seed's text always wins: a user can't change a record's text without
//     changing its Id, so differing text under the same Id can only be a seed fix
//     landing underneath.
//   * any other record → one of the user's own Records (added, or an edited copy).
//   * a seed record nothing in the catalogue matches → Removed.
public sealed class SeedDelta<T> where T : class
{
    // On-disk shape of a per-set delta file. Format 1 was the bare JSON array (a full
    // snapshot); Load migrates it.
    public sealed class DeltaFile
    {
        public int Format { get; init; } = 2;
        public List<string> Removed { get; init; } = [];
        public List<T> Overrides { get; init; } = [];
        public List<T> Records { get; init; } = [];
    }

    public sealed record MergeResult(List<T> Records, int OrphanedOverrides);

    public sealed record MigrationResult(DeltaFile Delta, int Kept, int FollowingSeed, int Removed, int NewInSeed);

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Func<T, string> _id;
    private readonly Func<T, string> _name;
    private readonly Func<T, IReadOnlyList<GameDataLink>?> _links;
    private readonly Func<T, T> _withoutLinks;
    private readonly Func<T, T, T> _applyOverride;

    // withoutLinks returns the record with Links nulled, so the rest compares by record
    // value equality while Links compare as a sequence. applyOverride(seed, user) copies
    // the user's editable fields onto the seed record, keeping the seed's identity text.
    public SeedDelta(
        Func<T, string> id,
        Func<T, string> name,
        Func<T, IReadOnlyList<GameDataLink>?> links,
        Func<T, T> withoutLinks,
        Func<T, T, T> applyOverride)
    {
        _id = id;
        _name = name;
        _links = links;
        _withoutLinks = withoutLinks;
        _applyOverride = applyOverride;
    }

    // ----- Pure diff / merge / migrate ---------

    public DeltaFile Diff(IEnumerable<T> current, IReadOnlyList<T> seed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(seed);
        SeedIndex index = new(this, seed);
        List<T> items = current.ToList();
        int[] slotOf = new int[items.Count];
        Array.Fill(slotOf, -1);
        bool[] claimed = new bool[seed.Count];

        // Exact matches claim first, so an Id-only fallback never takes a seed record a
        // later catalogue record matches exactly.
        for (int i = 0; i < items.Count; i++)
            Claim(i, index.Exact(_id(items[i]), LinkKey(_links(items[i]))));
        for (int i = 0; i < items.Count; i++)
            if (slotOf[i] < 0) Claim(i, index.ById(_id(items[i])));

        DeltaFile delta = new();
        for (int i = 0; i < items.Count; i++)
        {
            if (slotOf[i] < 0) { delta.Records.Add(items[i]); continue; }
            T baseline = seed[slotOf[i]];
            T merged = _applyOverride(baseline, items[i]);
            if (!SameContent(merged, baseline)) delta.Overrides.Add(merged);
        }
        for (int s = 0; s < seed.Count; s++)
            if (!claimed[s]) delta.Removed.Add(KeyOf(seed[s]));
        return delta;

        void Claim(int item, int slot)
        {
            if (slot < 0 || claimed[slot]) return;
            claimed[slot] = true;
            slotOf[item] = slot;
        }
    }

    // Seed order (minus Removed, Overrides applied), then the user's own records.
    public MergeResult Merge(IReadOnlyList<T> seed, DeltaFile delta)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(delta);
        SeedIndex index = new(this, seed);
        bool[] removed = new bool[seed.Count];
        foreach (string key in delta.Removed)
        {
            int bar = key.IndexOf('|', StringComparison.Ordinal);
            int s = bar < 0 ? index.ById(key) : index.Resolve(key[..bar], key[(bar + 1)..]);
            if (s >= 0) removed[s] = true;
        }

        T?[] overrides = new T?[seed.Count];
        int orphaned = 0;
        foreach (T o in delta.Overrides)
        {
            // An override whose seed record is gone (dropped from the seed, or re-linked
            // under a now-shared Id) has nothing left to apply to.
            int s = index.Resolve(_id(o), LinkKey(_links(o)));
            if (s < 0 || removed[s] || overrides[s] is not null) { orphaned++; continue; }
            overrides[s] = o;
        }

        List<T> own = [];
        foreach (T r in delta.Records)
        {
            // An own record the seed now carries verbatim (Id + Links) was folded into the
            // seed after the user saved it: adopt the seed's copy so it isn't listed twice.
            int s = index.Exact(_id(r), LinkKey(_links(r)));
            if (s >= 0 && !removed[s] && overrides[s] is null) { overrides[s] = r; continue; }
            own.Add(r);
        }

        List<T> records = new(seed.Count + own.Count);
        for (int s = 0; s < seed.Count; s++)
        {
            if (removed[s]) continue;
            records.Add(overrides[s] is { } o ? _applyOverride(seed[s], o) : seed[s]);
        }
        records.AddRange(own);
        return new MergeResult(records, orphaned);
    }

    // Convert a legacy full snapshot into a delta. A snapshot carries no removal info and
    // can't say which of its fields the user changed, so: a record whose Id is in the
    // seed follows the seed; any other record is kept as the user's own; a seed record
    // absent from the snapshot counts as removed only when a kept record has its Name and
    // Links (the user edited it into that copy) — otherwise it was added to the seed after
    // the user saved, and is included.
    public MigrationResult Migrate(IReadOnlyList<T> legacy, IReadOnlyList<T> seed)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(seed);
        HashSet<string> seedIds = seed.Select(_id).ToHashSet(StringComparer.Ordinal);
        HashSet<string> legacyIds = legacy.Select(_id).ToHashSet(StringComparer.Ordinal);

        DeltaFile delta = new();
        int following = 0;
        foreach (T r in legacy)
        {
            if (seedIds.Contains(_id(r))) following++;
            else delta.Records.Add(r);
        }

        HashSet<string> keptNameLinks = delta.Records.Select(NameLinkKey).ToHashSet(StringComparer.Ordinal);
        int newInSeed = 0;
        foreach (T s in seed)
        {
            if (legacyIds.Contains(_id(s))) continue;
            if (keptNameLinks.Contains(NameLinkKey(s))) delta.Removed.Add(KeyOf(s));
            else newInSeed++;
        }
        return new MigrationResult(delta, delta.Records.Count, following, delta.Removed.Count, newInSeed);
    }

    // ----- File I/O ---------

    // The catalogue at path layered over seed, or null when the file is missing or
    // unreadable (logged) so the caller falls back to the seed alone. A legacy snapshot
    // file is migrated once: backed up beside itself as "<file>.pre-delta", rewritten as
    // a delta, and the outcome logged.
    public List<T>? Load(string path, IReadOnlyList<T> seed, LogService? log, string category)
    {
        if (!File.Exists(path)) return null;
        List<T>? legacy;
        DeltaFile? delta;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), DocOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                legacy = doc.RootElement.Deserialize<List<T>>(JsonStore.Options) ?? [];
                delta = null;
            }
            else
            {
                legacy = null;
                delta = doc.RootElement.Deserialize<DeltaFile>(JsonStore.Options) ?? new DeltaFile();
            }
        }
        catch (Exception ex)
        {
            // Corrupt or schema-drifted file (e.g. a retired enum name) — surface it and
            // let the caller fall back to the seed rather than load a partial catalogue.
            log?.Log(LogSeverity.Warn, category, $"Failed to load '{path}': {ex.Message}");
            return null;
        }

        if (delta is null) return MigrateLegacy(path, legacy!, seed, log, category);

        MergeResult merged = Merge(seed, delta);
        if (merged.OrphanedOverrides > 0)
            log?.Log(LogSeverity.Info, category,
                $"'{path}': {merged.OrphanedOverrides} edit(s) apply to records no longer in the seed and were dropped.");
        return merged.Records;
    }

    public void Save(string path, IEnumerable<T> current, IReadOnlyList<T> seed, LogService? log, string category)
    {
        DeltaFile delta = Diff(current, seed);
        JsonStore.Save(path, delta);
        log?.Log(LogSeverity.Debug, category,
            $"saved '{path}': {delta.Records.Count} own record(s), {delta.Overrides.Count} override(s), " +
            $"{delta.Removed.Count} removed seed record(s); everything else follows the seed.");
    }

    private List<T> MigrateLegacy(string path, List<T> legacy, IReadOnlyList<T> seed, LogService? log, string category)
    {
        if (seed.Count == 0)
        {
            // Migrating against nothing would file every seed copy as the user's own.
            // Keep the snapshot until a launch that can read the seed.
            log?.Log(LogSeverity.Warn, category,
                $"'{path}' is an old full-snapshot file but no seed could be read — loaded as-is, not migrated.");
            return legacy;
        }

        MigrationResult m = Migrate(legacy, seed);
        string backup = path + ".pre-delta";
        try
        {
            if (!File.Exists(backup)) File.Copy(path, backup);
            JsonStore.Save(path, m.Delta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Log(LogSeverity.Warn, category,
                $"'{path}': couldn't rewrite the migrated file ({ex.Message}); migrated in memory, written on the next save.");
        }
        log?.Log(LogSeverity.Info, category,
            $"migrated '{path}' to edits-over-seed: kept {m.Kept} record(s) of your own, {m.FollowingSeed} now follow " +
            $"the seed, {m.Removed} seed record(s) stay removed (edited into your copies), {m.NewInSeed} seed record(s) " +
            $"added since the file was saved; old file backed up to '{backup}'.");
        return Merge(seed, m.Delta).Records;
    }

    // ----- Keys ---------

    private bool SameContent(T a, T b)
        => EqualityComparer<T>.Default.Equals(_withoutLinks(a), _withoutLinks(b))
           && LinkKey(_links(a), sorted: false) == LinkKey(_links(b), sorted: false);

    private string KeyOf(T r) => _id(r) + "|" + LinkKey(_links(r));

    private string NameLinkKey(T r) => _name(r).Trim().ToLowerInvariant() + "|" + LinkKey(_links(r));

    // Sorted for addressing (order carries no meaning there); unsorted for content
    // equality, so a reorder the user saved still counts as their edit.
    private static string LinkKey(IReadOnlyList<GameDataLink>? links, bool sorted = true)
    {
        if (links is null || links.Count == 0) return string.Empty;
        IEnumerable<string> parts = links.Select(l => $"{l.Table.ToLowerInvariant()}#{l.Number}");
        return string.Join(",", sorted ? parts.Order(StringComparer.Ordinal) : parts);
    }

    private sealed class SeedIndex
    {
        private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<int>> _byId = new(StringComparer.Ordinal);

        public SeedIndex(SeedDelta<T> spec, IReadOnlyList<T> seed)
        {
            for (int i = 0; i < seed.Count; i++)
            {
                _byKey.TryAdd(spec.KeyOf(seed[i]), i);
                string id = spec._id(seed[i]);
                if (!_byId.TryGetValue(id, out List<int>? slots)) _byId[id] = slots = [];
                slots.Add(i);
            }
        }

        public int Exact(string id, string linkKey)
            => _byKey.TryGetValue(id + "|" + linkKey, out int i) ? i : -1;

        public int ById(string id)
            => _byId.TryGetValue(id, out List<int>? slots) && slots.Count == 1 ? slots[0] : -1;

        public int Resolve(string id, string linkKey)
        {
            int exact = Exact(id, linkKey);
            return exact >= 0 ? exact : ById(id);
        }
    }
}
