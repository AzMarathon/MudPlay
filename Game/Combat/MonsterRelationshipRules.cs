using System.Globalization;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// One monster's relationship (Enemy / Neutral / Friend / Flee / Hangup), found by a
// name typed in chat and read or changed for the loaded character.
//
// A change is saved at the "only for this character" tier, the Relationship field
// alone, by the rule a remote item change is saved by
// (GameDataOverrideApplier.ApplyFieldsAt): it holds for this character whatever a
// lower tier says, and the record's other fields resolve as they did. Every engine
// that reads a relationship (combat targeting, the Flee / Hangup watch, self-defence)
// resolves the record on each use, so the change acts from the next room display or
// round, as a save in the Monster editor does.
public sealed class MonsterRelationshipRules
{
    private const string Table = "Monsters";

    private readonly SettingsResolver _resolver;
    private readonly MonsterCatalog _catalog;
    private readonly MonsterOverlaySeedStore _seed;

    public MonsterRelationshipRules(SettingsResolver resolver, MonsterCatalog catalog, MonsterOverlaySeedStore seed)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(seed);
        _resolver = resolver;
        _catalog = catalog;
        _seed = seed;
    }

    // The monsters a typed name stands for, by the rule the remote commands use for an
    // item's, a loop's or an event's name: the monster's whole name wins outright;
    // otherwise every monster whose name holds every typed word, in any order; and once
    // more without apostrophes, which typed chat drops. The monsters in play are
    // searched first and the rest only when none fits, so a placeholder record of the
    // same name doesn't make a real monster ambiguous. Same-named monsters are separate
    // records (a variant per area), so they stay several matches; `#<number>` names one
    // of them, in play or not. Sorted by name, then number.
    public IReadOnlyList<int> Find(string typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return Array.Empty<int>();
        string query = typed.Trim();
        if (query[0] == '#')
            return int.TryParse(query[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                && _catalog.Get(number) is not null
                ? new[] { number }
                : Array.Empty<int>();
        IReadOnlyList<int> inPlay = FindAmong(_catalog.InPlay, query);
        return inPlay.Count > 0 ? inPlay : FindAmong(_catalog.All, query);
    }

    private static IReadOnlyList<int> FindAmong(IEnumerable<MonsterCatalogEntry> monsters, string query)
    {
        List<MonsterCatalogEntry> candidates = monsters.Where(m => !string.IsNullOrWhiteSpace(m.Name)).ToList();
        List<MonsterCatalogEntry> found = candidates
            .Where(m => string.Equals(m.Name.Trim(), query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0)
            found = candidates.Where(m => RoomSearchService.NameMatchesTokens(m.Name, query)).ToList();
        if (found.Count == 0)
        {
            string bare = query.Replace("'", "");
            found = candidates.Where(m => RoomSearchService.NameMatchesTokens(m.Name.Replace("'", ""), bare)).ToList();
        }
        return found
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Number)
            .Select(m => m.Number)
            .ToList();
    }

    public string NameOf(int number) =>
        _catalog.Get(number)?.Name ?? $"monster #{number.ToString(CultureInfo.InvariantCulture)}";

    // Only a named character has a tier of its own to hold the change.
    public bool CanChange => _resolver.CanWriteAt(SettingsTier.Character);

    // The relationship as the engines read it: none set is Enemy.
    public MonsterRelationship Read(int number) => Resolve(number).Relationship ?? MonsterRelationship.Enemy;

    // The tier the record's values come from, for the log.
    public SettingsTier SourceOf(int number) => _resolver.GetGameDataSourceTier(Table, Id(number));

    // Set the relationship at the character tier and return it as it now resolves.
    public MonsterRelationship Change(int number, MonsterRelationship relationship)
    {
        // The installed default is compared as the engines read it, so setting Enemy on a
        // monster with no relationship installed, and nothing below saying otherwise,
        // leaves this character no override rather than a copy of the default.
        MonsterOverlay seed = _seed.GetOverlay(number);
        MonsterOverlay installed = seed with { Relationship = seed.Relationship ?? MonsterRelationship.Enemy };
        GameDataOverrideApplier.ApplyFieldsAt(_resolver, Table, Id(number), SettingsTier.Character,
            installed with { Relationship = relationship }, installed, [nameof(MonsterOverlay.Relationship)]);
        return Read(number);
    }

    private MonsterOverlay Resolve(int number) =>
        _resolver.ResolveGameData(Table, Id(number), _seed.GetOverlay(number));

    private static string Id(int number) => number.ToString(CultureInfo.InvariantCulture);
}
