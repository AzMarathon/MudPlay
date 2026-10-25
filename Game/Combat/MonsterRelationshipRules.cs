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

    // The monsters a typed name stands for, by the rule the remote commands share for
    // a name (RoomSearchService.FindAllByName): the whole name, then every typed word
    // in any order, then the same without apostrophes. At each step a monster in play
    // comes before one out of play (the Unobtainable list), so a placeholder record of
    // a real monster's name doesn't make it ambiguous, and one out of play is still
    // found when nothing in play fits. Same-named monsters are separate records (a
    // variant per area), so they stay several matches; `#<number>` names one of them,
    // in play or not. Sorted by name, then number.
    public IReadOnlyList<int> Find(string typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return Array.Empty<int>();
        string query = typed.Trim();
        if (query[0] == '#')
            return int.TryParse(query[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                && _catalog.Get(number) is not null
                ? new[] { number }
                : Array.Empty<int>();
        HashSet<int> inPlay = _catalog.InPlay.Select(m => m.Number).ToHashSet();
        return RoomSearchService.FindAllByName(_catalog.All, m => m.Name, query, m => inPlay.Contains(m.Number))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Number)
            .Select(m => m.Number)
            .ToList();
    }

    // The monster's name in the game data; empty when the record has none.
    public string NameOf(int number) => _catalog.Get(number)?.Name.Trim() ?? string.Empty;

    // Only a named character has a tier of its own to hold the change.
    public bool CanChange => _resolver.CanWriteAt(SettingsTier.Character);

    // The relationship as the engines read it: a monster given none by an override,
    // the seed or its alignment reads Enemy.
    public MonsterRelationship Read(int number) => Resolve(number).Relationship ?? MonsterRelationship.Enemy;

    // Where the Relationship itself comes from, for the log: an override tier, the
    // seed or the monster's alignment.
    public string RelationshipSourceOf(int number) => _seed.RelationshipSourceOf(_resolver, number);

    // Set the relationship at the character tier and return it as it now resolves.
    public MonsterRelationship Change(int number, MonsterRelationship relationship)
    {
        // The installed default (the seed's, or the alignment's for a monster the
        // seed doesn't know) is compared as the engines read it, so setting Enemy on a
        // monster with no relationship installed, and nothing below saying otherwise,
        // leaves this character no override rather than a copy of the default.
        MonsterOverlay seed = _seed.GetOverlay(number);
        MonsterOverlay installed = seed with { Relationship = seed.Relationship ?? MonsterRelationship.Enemy };
        GameDataOverrideApplier.ApplyFieldsAt(_resolver, Table, Id(number), SettingsTier.Character,
            installed with { Relationship = relationship }, installed, [nameof(MonsterOverlay.Relationship)]);
        return Read(number);
    }

    private MonsterOverlay Resolve(int number) => _seed.Resolve(_resolver, number);

    private static string Id(int number) => number.ToString(CultureInfo.InvariantCulture);
}
