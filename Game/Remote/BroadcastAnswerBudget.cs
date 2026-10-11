namespace MudPlay.Game.Remote;

// How much this client says on the numbered broadcast channel in answer to other
// players. The channel is public: anyone can join it, a player with no permission
// here is still answered (the Failure message), and every line said there counts
// against the user's own input with the game's limiter (GAME_MECHANICS "Command
// rate limit"). So the first answer is given, as on any channel, and the rest are
// bounded:
//   - one answer a minute to each speaker, whatever it is: a timer sync's data, a
//     "works by … only" refusal or the Failure message;
//   - AnswersPerWindow answers a minute to everyone together, so a channel full of
//     speakers cannot make the client flood either.
// An answer is everything said for one line of theirs. A timer sync's data is
// several lines to one asker and counts once, so it is never cut in the middle.
internal sealed class BroadcastAnswerBudget
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    // Small, and still room for a few players syncing timers in the same minute.
    // A full timer answer is 5 lines (60 timers by monster number), so at most
    // 20 lines a minute are said here for real data and 4 for refusals.
    public const int AnswersPerWindow = 4;

    // When each speaker was last answered. Names older than the window are dropped
    // on every look and none is added past the cap, so this never holds more than
    // AnswersPerWindow of them.
    private readonly Dictionary<string, DateTimeOffset> _answeredAt = new(StringComparer.OrdinalIgnoreCase);

    public int Remembered => _answeredAt.Count;

    // Why speaker would not be answered now, for the log, or null when they would.
    public string? WhyNot(string speaker, DateTimeOffset now)
    {
        foreach (string stale in _answeredAt.Where(p => now - p.Value >= Window).Select(p => p.Key).ToList())
            _answeredAt.Remove(stale);
        if (_answeredAt.ContainsKey(speaker)) return "they were answered there less than a minute ago";
        if (_answeredAt.Count >= AnswersPerWindow)
            return $"{AnswersPerWindow} answers were given there in the last minute";
        return null;
    }

    // An answer to speaker begins now. Only after WhyNot said they may have one.
    public void Note(string speaker, DateTimeOffset now) => _answeredAt[speaker] = now;
}
