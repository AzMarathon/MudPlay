using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace MudPlay.Services;

// One string for each distinct text the structures built from a game-data load keep.
// A set has fifteen rooms to a name and thousands of monsters share a few attack
// names, and each reader of a table used to keep its own copy of each. The room graph
// (names, lair tags, exit hints), RoomNameIndex and MonsterCatalog (attack names) keep
// theirs through the pool, so a name the graph and the index both hold is one string.
//
// One pool per load of a set (GameDataCache.Strings), never string.Intern: a set
// switch or reload starts a new pool, so the old set's strings go once the structures
// built from it do. Texts are kept exactly, case and whitespace included, and a pooled
// string is an ordinary immutable string, so nothing that compares or copies one can
// tell it from a fresh copy. Thread-safe: the room graph reads its rows in parallel.
public sealed class StringPool
{
    private readonly ConcurrentDictionary<string, string> _texts = new(StringComparer.Ordinal);

    // Distinct texts held.
    public int Count => _texts.Count;

    [return: NotNullIfNotNull(nameof(text))]
    public string? Get(string? text) => text is null ? null : _texts.GetOrAdd(text, text);
}
