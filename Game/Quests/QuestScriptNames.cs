using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// Names for the record numbers a script line carries, from the active set. A number the set
// has no record for keeps its number ("Item #998"), so nothing is silently dropped.
public sealed class QuestScriptNames
{
    private readonly GameDataCache _cache;

    public QuestScriptNames(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    public string Item(int number) => TBInfoActionDecoder.Named(_cache, "Items", number, "Item");
    public string Monster(int number) => TBInfoActionDecoder.Named(_cache, "Monsters", number, "Monster");
    public string Spell(int number) => TBInfoActionDecoder.Named(_cache, "Spells", number, "Spell");
    public string Class(int number) => TBInfoActionDecoder.Named(_cache, "Classes", number, "Class");
    public string Race(int number) => TBInfoActionDecoder.Named(_cache, "Races", number, "Race");
    public static string Ability(int number) => AbilityNames.FormatId(number);

    // Asked each time and not kept: a room the data leaves unnamed answers to the
    // name the client learned for it, which changes with the realm.
    public string Room(int map, int room)
        => RoomNameIndex.NameOf(_cache, map, room) is { Length: > 0 } name
            ? name : $"Room {map}/{room}";
}
