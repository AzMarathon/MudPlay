namespace MudPlay.Game.Pvp;

// Who dropped out of our party a moment ago. A teleport that splits the party puts
// everyone in the same room again a beat later, outside the party: a room attack
// cast then hits them, and one of theirs hits us, with nobody meaning it. So for a
// while after a member leaves, our room attacks are held and theirs isn't read as
// an attack. A member who rejoins is no longer out.
public sealed class PartySplitTracker : IDisposable
{
    private readonly PartyState _party;
    private readonly Func<TimeSpan> _hold;
    private readonly Func<string?> _ownGivenName;
    private readonly Func<DateTimeOffset> _now;
    private readonly HashSet<string> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _leftAt = new(StringComparer.OrdinalIgnoreCase);

    public PartySplitTracker(
        PartyState party, Func<TimeSpan> hold, Func<string?> ownGivenName, Func<DateTimeOffset>? now = null)
    {
        _party = party;
        _hold = hold;
        _ownGivenName = ownGivenName;
        _now = now ?? (() => DateTimeOffset.Now);
        foreach (PartyMember m in _party.Members) _members.Add(GivenOf(m));
        _party.Members.CollectionChanged += OnPartyChanged;
    }

    // Whether this player left our party within the hold and hasn't rejoined.
    public bool LeftRecently(string given) =>
        _leftAt.TryGetValue(given, out DateTimeOffset at) && _now() - at < _hold();

    // The first such player, or null: the party is whole, or the hold has run out.
    public string? AnyoneOut()
    {
        TimeSpan hold = _hold();
        DateTimeOffset now = _now();
        string? own = _ownGivenName();
        foreach ((string given, DateTimeOffset at) in _leftAt)
            if (now - at < hold && !string.Equals(given, own, StringComparison.OrdinalIgnoreCase))
                return given;
        return null;
    }

    // How long ago they left, for the log.
    public TimeSpan? Since(string given) =>
        _leftAt.TryGetValue(given, out DateTimeOffset at) ? _now() - at : null;

    // The roster is rebuilt wholesale at times (a Reset carries no old items), so
    // departures are found by comparing against our own copy of it. Our own row
    // going (we left, or were dropped) takes everyone else's with it.
    private void OnPartyChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        HashSet<string> current = new(StringComparer.OrdinalIgnoreCase);
        foreach (PartyMember m in _party.Members) current.Add(GivenOf(m));

        DateTimeOffset now = _now();
        foreach (string was in _members)
            if (!current.Contains(was)) _leftAt[was] = now;
        foreach (string back in current) _leftAt.Remove(back);

        _members.Clear();
        _members.UnionWith(current);
    }

    private static string GivenOf(PartyMember member) =>
        Models.GameData.PlayerObservation.SplitName(member.Name).Given;

    public void Dispose() => _party.Members.CollectionChanged -= OnPartyChanged;
}
