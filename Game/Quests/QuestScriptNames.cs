using MudPlay.Game.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// Names for the record numbers a script line carries, from the active set. A number the set
// has no record for keeps its number ("Item #998"), so nothing is silently dropped.
public sealed class QuestScriptNames
{
    private readonly GameDataCache _cache;
    private readonly IReadOnlyDictionary<(int Map, int Room), string> _rooms;

    public QuestScriptNames(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _rooms = RoomNameIndex.For(cache);
    }

    public string Item(int number) => Named("Items", number, $"Item #{number}");
    public string Monster(int number) => Named("Monsters", number, $"Monster #{number}");
    public string Spell(int number) => Named("Spells", number, $"Spell #{number}");
    public string Class(int number) => Named("Classes", number, $"Class #{number}");
    public string Race(int number) => Named("Races", number, $"Race #{number}");
    public static string Ability(int number) => AbilityNames.FormatId(number);

    public string Room(int map, int room)
        => _rooms.TryGetValue((map, room), out string? name) && name.Length > 0
            ? name : $"Room {map}/{room}";

    private string Named(string table, int number, string fallback)
        => _cache.FindNameByNumber(table, number) is { Length: > 0 } n ? n : fallback;
}
