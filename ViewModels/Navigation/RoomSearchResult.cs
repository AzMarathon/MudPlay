using MudPlay.Game.Map;

namespace MudPlay.ViewModels.Navigation;

// Which source surfaced a search result. Drives the PRIMARY grouping of the
// dropdown — saved GOTO favourites first, then boss-table targets, then rooms
// found by name / coordinate / raw game data, then bare monster labels — ahead
// of the within-group relevance/distance sort. The enum's declaration order IS
// the group order (sorted by its int value), so keep it intentional.
public enum SearchResultKind
{
    Favorite = 0,
    Boss     = 1,
    Room     = 2,
    Monster  = 3,
}

// One entry in the Navigation right-rail search results list. Carries just
// enough to render a row (primary + secondary line + optional step distance)
// and the key the user-pick callback needs.
//
// Two row shapes share this record because the dropdown renders them in a
// single uniform template:
//   - Plain room match: MonsterTag is null. PrimaryLine shows "M/R - Name",
//     SecondaryLine the step distance.
//   - Monster-room match: MonsterTag set (e.g. "Goblin Warrior · regen 4h").
//     PrimaryLine shows the monster header, SecondaryLine the room reference
//     + step distance. Multiple rooms hosting the same monster surface as
//     multiple entries — clicking one queues that specific room.
public sealed record RoomSearchResult(
    RoomKey Key,
    string Name,
    int? StepsFromCurrent,
    string? MonsterTag = null,
    // Relevance rank for result ordering (lower sorts first): 0 = exact / literal
    // whole-word match, higher = the query only appears as a buried substring (e.g.
    // "aged" inside "Ravaged"). Set by RoomSearchService's token tiers; coordinate /
    // acronym matches keep the default 0 so they lead.
    int MatchRank = 0,
    // Source group for the primary dropdown ordering. Defaults to Room so the
    // room-name / coordinate tiers need no explicit stamp; the favourite / boss /
    // monster tiers set their own.
    SearchResultKind Kind = SearchResultKind.Room)
{
    // Legacy alias for older bindings — same as PrimaryLine's room form.
    public string DisplayName => $"{Key.Map}/{Key.Room} - {Name}";

    // Legacy sublabel for older bindings.
    public string DisplayLocation => StepsFromCurrent switch
    {
        null => string.Empty,
        0    => "here",
        1    => "1 step",
        _    => $"{StepsFromCurrent} steps",
    };

    // Top line in the dropdown row. Monster tag when present, otherwise the room reference.
    public string PrimaryLine => MonsterTag ?? $"{Key.Map}/{Key.Room} - {Name}";

    // True when this row carries no walkable destination — used for monster
    // matches whose lair isn't recorded in game data (unique bosses,
    // wandering spawns). The click handler skips these so they behave as
    // informational labels in the dropdown.
    public bool IsInformational => Key.Map <= 0 || Key.Room <= 0;

    // Bottom line: when this is a monster match, the underlying room; otherwise the step distance.
    public string SecondaryLine => MonsterTag is null
        ? DisplayLocation
        : (IsInformational
            ? "(no known lair location)"
            : StepsFromCurrent switch
            {
                null => $"{Key.Map}/{Key.Room} - {Name}",
                0    => $"{Key.Map}/{Key.Room} - {Name} · here",
                1    => $"{Key.Map}/{Key.Room} - {Name} · 1 step",
                _    => $"{Key.Map}/{Key.Room} - {Name} · {StepsFromCurrent} steps",
            });
}
