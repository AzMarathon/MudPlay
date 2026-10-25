using System.IO;
using System.Text.Json;
using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// In-memory cache of the active game-data set's Defaults-tier MonsterOverlay — the
// baseline for per-monster automation behavior (relationship / priority /
// DontBackstab) and location labels (Landmass / Region / Area) before any user
// Global / BBS / Character override is applied.
//
// Two sources make it, the seed first:
//   - the realm's shipped seed. Each realm family (stock MajorMUD, Paradigm, …)
//     ships its own decoded-from-Monsters.md seed file under
//     AppPaths.BundledMonsterOverlaySeedFile; the active set's Info.json[0].Legit
//     picks which: 2 → paradigm, anything else (or unreadable) → stock.
//   - the monster's own Align in the set's Monsters table, for the Relationship
//     of every monster the seed leaves without one (GAME_MECHANICS "Monster
//     `Align` values and your alignment-title ladder", Client policy 2026-10-25).
//
// A seed entry is keyed by number, and a set that renumbers (a custom realm built on
// another's data) can put a different monster there. So an entry applies only when
// its Name is the set's name for that number; otherwise the whole entry is left out,
// its location labels, priority and DontBackstab with its relationship, since they
// describe the other monster, and the set's monster falls to its alignment.
//
// Wiring: AppServices subscribes Load to GameDataCache.ActiveSetChanged — every
// set switch or re-import rereads Info.json and the seed and walks the Monsters
// table once. Consumers call GetOverlay for a monster Number; that overlay is then
// passed to SettingsResolver.ResolveGameData as the Defaults-tier record over which
// higher-tier deltas are merged (Resolve does both). Each load is built aside and
// published as one immutable snapshot, so a reader on another thread (the
// Simulator) sees one load or the next, never a half-built one.
//
// The seed file itself is never written by the app. To reset a seed, delete the
// user-writable copy at AppPaths.MonsterOverlaySeedFile and relaunch —
// AppPaths.EnsureGlobalSeedsBootstrapped re-copies it from the bundled source.
public sealed class MonsterOverlaySeedStore
{
    private const string LogSource = "MonsterOverlaySeed";
    private const string MonstersTable = "Monsters";

    private readonly GameDataCache _gameData;
    private readonly LogService? _log;
    private Snapshot _current = Snapshot.Empty;

    // Realm flavor currently sourcing the cache, or null when none loaded.
    public string? ActiveRealm { get; private set; }

    // Set name currently sourcing the cache, or null when none active.
    public string? ActiveSet { get; private set; }

    // Distinct, sorted location labels the seed carries — the typeahead lists for the
    // monster record's Landmass / Region / Area boxes, so a new monster is filed under
    // an existing name rather than a near-miss spelling.
    public MonsterLocationSuggestions LocationSuggestions => _current.Suggestions;

    // A short hash of the Defaults tier as it is held here, "none" with nothing
    // loaded. For whoever keeps a result that depends on it beyond this session (the
    // Simulator's rankings, grouped and filtered by these records): taken from what
    // was loaded, not from the file, which an update or another client's launch can
    // replace while this client goes on with the records it read. The relationships
    // taken from alignment are in it, being which monsters are fought.
    public string ContentStamp => _current.Stamp;

    // Where the relationships of the active set came from, for the bug report: the
    // same counts the load's Info line gives.
    public string RelationshipSummary => _current.Summary;

    public MonsterOverlaySeedStore(GameDataCache gameData, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        _gameData = gameData;
        _log = log;
    }

    // Switch the cache to setName: its realm's seed under its own monsters'
    // alignments. Pass null to clear (no set active). An unreadable Info.json or seed
    // file leaves the seed empty with a log entry; an unreadable Monsters table
    // leaves the seed unchecked by name and nothing taken from alignment.
    public void Load(string? setName)
    {
        ActiveSet = setName;
        ActiveRealm = null;
        if (string.IsNullOrWhiteSpace(setName))
        {
            _current = Snapshot.Empty;
            return;
        }

        string realm = ResolveRealm(setName);
        ActiveRealm = realm;
        List<SeedRecord> seed = ReadSeed(realm);
        Dictionary<int, SetMonster>? monsters = ReadMonsters(setName);

        Dictionary<int, MonsterOverlay> byNumber = new();
        Dictionary<int, int> fromAlignment = new();
        List<string> nameDiffers = new();
        foreach (SeedRecord rec in seed)
        {
            if (rec.Number <= 0) continue;
            if (monsters is not null && monsters.TryGetValue(rec.Number, out SetMonster held)
                && !string.IsNullOrWhiteSpace(rec.Name) && held.Name.Length > 0
                && !string.Equals(rec.Name.Trim(), held.Name, StringComparison.OrdinalIgnoreCase))
            {
                nameDiffers.Add($"#{rec.Number} seed '{rec.Name.Trim()}', set '{held.Name}'");
                continue;
            }
            // The seed's Name is there to check it against the set and for a person
            // reading the file. The overlay's Name is a user-override hook, not a
            // label: carried here it would read as a Defaults-tier rename of every
            // seeded monster.
            byNumber[rec.Number] = new MonsterOverlay
            {
                Relationship = rec.Relationship,
                Priority     = rec.Priority,
                DontBackstab = rec.DontBackstab,
                Landmass     = NullIfBlank(rec.Landmass),
                Region       = NullIfBlank(rec.Region),
                Area         = NullIfBlank(rec.Area),
            };
        }
        // Counted among the set's own monsters: a seed carries numbers some sets lack.
        int fromSeed = byNumber.Count(kv => kv.Value.Relationship is not null
                                            && (monsters is null || monsters.ContainsKey(kv.Key)));

        int noAlign = 0;
        if (monsters is not null)
            foreach ((int number, SetMonster m) in monsters)
            {
                byNumber.TryGetValue(number, out MonsterOverlay? seeded);
                if (seeded?.Relationship is not null) continue;
                if (m.Align is not { } align || MonsterEngagement.DefaultForAlignment(align) is not { } derived)
                {
                    noAlign++;
                    continue;
                }
                byNumber[number] = (seeded ?? new MonsterOverlay()) with { Relationship = derived };
                fromAlignment[number] = align;
            }

        int Derived(MonsterRelationship r) =>
            fromAlignment.Count(kv => MonsterEngagement.DefaultForAlignment(kv.Value) == r);
        string summary = monsters is null
            ? $"{fromSeed} from the seed; none from alignment (no Monsters table); seed not checked by name"
            : $"{fromSeed} from the seed, {fromAlignment.Count} from alignment "
              + $"({Derived(MonsterRelationship.Friend)} friend / {Derived(MonsterRelationship.Neutral)} neutral / "
              + $"{Derived(MonsterRelationship.Enemy)} enemy), {noAlign} with no alignment (Enemy), "
              + $"{nameDiffers.Count} seed entries skipped (name differs)";

        _current = new Snapshot(
            byNumber,
            fromAlignment,
            new MonsterLocationSuggestions(
                DistinctSorted(byNumber.Values.Select(o => o.Landmass)),
                DistinctSorted(byNumber.Values.Select(o => o.Region)),
                DistinctSorted(byNumber.Values.Select(o => o.Area))),
            StampOf(byNumber),
            summary);

        _log?.Log(LogSeverity.Info, LogSource,
            $"Monster relationships for '{setName}' (realm '{realm}'): {summary}; "
            + $"{byNumber.Values.Count(o => o.Region is not null)} with a location.");
        if (nameDiffers.Count > 0)
            _log?.Debug(LogSource, "Seed entries left out, a different monster in this set: "
                + string.Join("; ", nameDiffers.Take(5)) + (nameDiffers.Count > 5 ? "; …" : ""));
    }

    internal static string StampOf(IEnumerable<KeyValuePair<int, MonsterOverlay>> records)
    {
        var text = new System.Text.StringBuilder();
        foreach ((int number, MonsterOverlay o) in records.OrderBy(r => r.Key))
            text.Append(number).Append('|').Append(o.Relationship?.ToString()).Append('|').Append(o.Priority?.ToString())
                .Append('|').Append(o.DontBackstab?.ToString()).Append('|').Append(o.Landmass).Append('|').Append(o.Region)
                .Append('|').Append(o.Area).Append('\n');
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    // The overlay every engine reads for a monster: this Defaults tier under the
    // Global / BBS / Character overrides.
    public MonsterOverlay Resolve(SettingsResolver resolver, int monsterNumber) =>
        resolver.ResolveGameData(MonstersTable, Id(monsterNumber), GetOverlay(monsterNumber));

    // Defaults-tier overlay for monsterNumber; a blank MonsterOverlay for a monster
    // neither the seed nor an alignment says anything about, which the engines read
    // as Enemy / Normal / no flags.
    public MonsterOverlay GetOverlay(int monsterNumber) =>
        _current.ByNumber.TryGetValue(monsterNumber, out MonsterOverlay? overlay)
            ? overlay
            : new MonsterOverlay();

    // Where the monster's relationship as the engines read it comes from: the
    // highest override tier that holds one, else the Defaults tier's own source.
    // For the bug report and the @relation log.
    public string RelationshipSourceOf(SettingsResolver resolver, int monsterNumber)
    {
        SettingsTier tier = RelationshipTierOf(resolver, monsterNumber);
        if (tier != SettingsTier.Defaults) return $"the {tier} tier";
        Snapshot now = _current;
        if (now.FromAlignment.TryGetValue(monsterNumber, out int align))
            return $"its alignment, {LookupEnums.FormatMonAlignment(Id(align))}";
        return now.ByNumber.TryGetValue(monsterNumber, out MonsterOverlay? o) && o.Relationship is not null
            ? "the seed"
            : "no seed or alignment";
    }

    // The tier the Relationship itself comes from: the highest that holds one, or
    // Defaults when none does. Other fields of the record may come from other tiers.
    private static SettingsTier RelationshipTierOf(SettingsResolver resolver, int monsterNumber)
    {
        foreach (SettingsTier tier in new[] { SettingsTier.Character, SettingsTier.Bbs, SettingsTier.Global })
            if (resolver.ReadGameDataAt(tier, MonstersTable, Id(monsterNumber)) is { } held
                && held[nameof(MonsterOverlay.Relationship)] is not null)
                return tier;
        return SettingsTier.Defaults;
    }

    private static string Id(int number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private List<SeedRecord> ReadSeed(string realm)
    {
        // Per-realm user-writable seed at Global/MonsterOverlay.{realm}.seed.json.
        // Bootstrapping from the bundled Defaults/ copy happens once at app
        // startup via AppPaths.EnsureGlobalSeedsBootstrapped().
        string path = AppPaths.MonsterOverlaySeedFile(realm);
        if (!File.Exists(path))
        {
            _log?.Log(LogSeverity.Info, LogSource, $"No seed file at '{path}' for realm '{realm}'; using an empty seed.");
            return new List<SeedRecord>();
        }
        try
        {
            List<SeedRecord> records = JsonStore.Load<List<SeedRecord>>(path) ?? new List<SeedRecord>();
            _log?.Debug(LogSource, $"Read {records.Count} seed records from '{path}' (realm '{realm}').");
            return records;
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, LogSource, $"Failed to load '{path}': {ex.Message}");
            return new List<SeedRecord>();
        }
    }

    // Each monster's name and Align in the set, read once per load; null when the set
    // has no readable Monsters table. The first row of a number wins, as the cache's
    // own lookups do.
    private Dictionary<int, SetMonster>? ReadMonsters(string setName)
    {
        GameTable? table = _gameData.GetTable(MonstersTable);
        if (table is null)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                $"Set '{setName}' has no Monsters table: the seed is used unchecked and no relationship is taken from alignment.");
            return null;
        }
        Dictionary<int, SetMonster> monsters = new();
        foreach (GameRow row in table.Rows)
        {
            if (!TryInt(row, "Number", out int number) || number <= 0 || monsters.ContainsKey(number)) continue;
            string name = row.TryGetProperty("Name", out GameValue n) && n.ValueKind == JsonValueKind.String
                ? (n.GetString() ?? string.Empty).Trim()
                : string.Empty;
            monsters[number] = new SetMonster(name, TryInt(row, "Align", out int align) ? align : null);
        }
        _gameData.EvictTable(MonstersTable);
        return monsters;
    }

    // Duplicated from the other Monsters-table readers: each keeps its own private copy.
    private static bool TryInt(GameRow row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out GameValue el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IReadOnlyList<string> DistinctSorted(IEnumerable<string?> values) =>
        values.Where(v => v is not null).Select(v => v!)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .Order(StringComparer.OrdinalIgnoreCase)
              .ToArray();

    // Reads Info.json[0].Legit from the set's folder and maps to a realm name.
    private string ResolveRealm(string setName)
    {
        string infoPath = Path.Combine(_gameData.GameDataRoot, setName, "Info.json");
        if (!File.Exists(infoPath))
        {
            _log?.Log(LogSeverity.Info, LogSource, $"No Info.json at '{infoPath}'; defaulting realm to 'stock'.");
            return "stock";
        }

        try
        {
            using FileStream fs = File.OpenRead(infoPath);
            using JsonDocument doc = JsonDocument.Parse(fs);
            // Info.json is an array with one object holding the export metadata.
            JsonElement root = doc.RootElement;
            JsonElement first =
                root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                    ? root[0]
                    : root;
            if (first.TryGetProperty("Legit", out JsonElement legit)
                && legit.TryGetInt32(out int legitValue))
            {
                return legitValue == 2 ? "paradigm" : "stock";
            }
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, LogSource,
                $"Failed to parse '{infoPath}': {ex.Message}; defaulting realm to 'stock'.");
        }
        return "stock";
    }

    private readonly record struct SetMonster(string Name, int? Align);

    // One load, published whole. FromAlignment maps each monster whose relationship
    // was taken from its alignment to that Align.
    private sealed record Snapshot(
        IReadOnlyDictionary<int, MonsterOverlay> ByNumber,
        IReadOnlyDictionary<int, int> FromAlignment,
        MonsterLocationSuggestions Suggestions,
        string Stamp,
        string Summary)
    {
        public static readonly Snapshot Empty = new(
            new Dictionary<int, MonsterOverlay>(), new Dictionary<int, int>(),
            MonsterLocationSuggestions.Empty, "none", "no game-data set loaded");
    }

    // Wire shape on disk. Mirrors Defaults/MonsterOverlay.{realm}.seed.json's
    // JSON layout — Number + Name + the overridable seed fields.
    private sealed record SeedRecord
    {
        public int                    Number       { get; init; }
        public string?                Name         { get; init; }
        public MonsterRelationship?   Relationship { get; init; }
        public MonsterAttackPriority? Priority     { get; init; }
        public bool?                  DontBackstab { get; init; }
        public string?                Landmass     { get; init; }
        public string?                Region       { get; init; }
        public string?                Area         { get; init; }
    }
}
