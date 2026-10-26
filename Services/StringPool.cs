using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace MudPlay.Services;

// One string for each distinct text the structures built from a game-data load keep.
// A set has fifteen rooms to a name and TBInfo repeats a handful of action verbs
// thousands of times, and every reader of a table (the room graph, the room-name and
// spawn indexes, TBInfo) used to keep its own copy of each: half the strings a loaded
// set held were copies.
//
// One pool per load of a set (GameDataCache.Strings), never string.Intern: a set
// switch or reload starts a new pool, so the old set's strings go once the structures
// built from it do. Texts are kept exactly, case and whitespace included, and a pooled
// string is an ordinary immutable string, so nothing that compares or copies one can
// tell it from a fresh copy. Thread-safe: the room graph reads its rows in parallel.
public sealed class StringPool
{
    // Short texts are decoded on the stack; a longer one (a lair's group list runs to
    // tens of KB) goes through a rented buffer.
    private const int StackChars = 256;

    private readonly ConcurrentDictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _bySpan;

    public StringPool() => _bySpan = _texts.GetAlternateLookup<ReadOnlySpan<char>>();

    // Distinct texts held.
    public int Count => _texts.Count;

    [return: NotNullIfNotNull(nameof(text))]
    public string? Get(string? text) => text is null ? null : _texts.GetOrAdd(text, text);

    public string Get(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return string.Empty;
        if (_bySpan.TryGetValue(text, out string? kept)) return kept;
        string made = text.ToString();
        return _texts.GetOrAdd(made, made);
    }

    // The text of these UTF-8 bytes, decoded as Encoding.UTF8.GetString decodes them
    // (an invalid sequence becomes U+FFFD in both), with no string made when the text
    // is already held.
    public string GetUtf8(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return string.Empty;
        int most = Encoding.UTF8.GetMaxCharCount(utf8.Length);
        char[]? rented = null;
        Span<char> chars = most <= StackChars ? stackalloc char[StackChars] : (rented = ArrayPool<char>.Shared.Rent(most));
        try
        {
            int length = Encoding.UTF8.GetChars(utf8, chars);
            return Get(chars[..length]);
        }
        finally
        {
            if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        }
    }
}
