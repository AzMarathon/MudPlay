using System.Collections.Specialized;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Tokens;

// The lines a token use prints, read from the token spells' records in the Messages
// catalogue so the wording lives in one place: our own use (the record's caster
// line) and a use someone in the room makes (its witness line).
//
// Our own line names no one ("You invoke the token and summon a gryphon to
// Arlysia!"), and a template with no placeholder makes no CasterMessageMatcher, so
// it is the whole line, word for word. Built through the matcher alone, every token
// record was passed over and no use of ours was ever recognized: the buff hold
// waited out its timeout and nothing heard of the teleport (report
// paradigm-20261022-164244, a token of Arlysia).
public sealed class TokenUseLines
{
    private readonly MessageStore _messages;
    private List<Entry>? _entries;

    private readonly record struct Entry(string Place, string? OwnLine, CasterMessageMatcher? Own, CasterMessageMatcher? Witness);

    public TokenUseLines(MessageStore messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = messages;
        // Read again whenever the catalogue changes: a set's load, an edit.
        _messages.Messages.CollectionChanged += OnMessagesChanged;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => _entries = null;

    // Our own token use → the token's place, or null.
    public string? OwnUse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        string trimmed = line.Trim();
        foreach (Entry entry in Entries())
        {
            if (entry.Own is { } own ? own.TryMatch(line, out _) : string.Equals(trimmed, entry.OwnLine, StringComparison.Ordinal))
                return entry.Place;
        }
        return null;
    }

    // Someone else's token use → their name (the witness line's {source}), or null.
    public string? WitnessedUse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        foreach (Entry entry in Entries())
            if (entry.Witness is { } witness && witness.TryMatch(line, out IReadOnlyList<string> caps) && caps.Count > 0)
                return caps[0];
        return null;
    }

    private List<Entry> Entries()
    {
        if (_entries is { } built) return built;
        List<Entry> entries = new();
        foreach (MessageRecord r in _messages.Messages)
        {
            if (TokenCatalog.PlaceOf(r.Name) is not { } place) continue;
            CasterMessageMatcher? own = CasterMessageMatcher.TryCreate(r.CasterMessage);
            string? ownLine = own is null && !MessageRecord.IsBlankOrAbsent(r.CasterMessage) ? r.CasterMessage.Trim() : null;
            CasterMessageMatcher? witness = CasterMessageMatcher.TryCreate(r.WitnessMessage);
            if (own is null && ownLine is null && witness is null) continue;
            entries.Add(new Entry(place, ownLine, own, witness));
        }
        return _entries = entries;
    }
}
