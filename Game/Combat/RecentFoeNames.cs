namespace MudPlay.Game.Combat;

// The monsters named by the last few seconds of damage lines: who we hit, and who
// hit us. It is the one thing that still names a monster when the room is too dark
// to list what is in it and its death line isn't on record, which is how a boss
// killed in a pitch-black den went unmarked (the Darken Beast Lord's Dark Den).
public sealed class RecentFoeNames
{
    private readonly Func<string, bool> _isPlayer;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.OrdinalIgnoreCase);

    // isPlayer: the name is ours, a party member's or another player's.
    public RecentFoeNames(Func<string, bool> isPlayer, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(isPlayer);
        _isPlayer = isPlayer;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public void Note(DamageAttribution sides)
    {
        Note(sides.Source);
        Note(sides.Target);
    }

    private void Note(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string trimmed = name.Trim();
        if (trimmed.Equals("you", StringComparison.OrdinalIgnoreCase) || _isPlayer(trimmed)) return;
        _seen[trimmed] = _now();
    }

    // The monsters named within the window, most recent state only.
    public IReadOnlyCollection<string> Within(TimeSpan window)
    {
        DateTimeOffset cutoff = _now() - window;
        foreach (string old in _seen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            _seen.Remove(old);
        return _seen.Keys.ToList();
    }

    public void Clear() => _seen.Clear();
}
