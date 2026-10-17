namespace MudPlay.Game.Remote;

// How much this client says on the numbered broadcast channel in answer to other
// players. The channel is public: anyone can join it, a player with no permission
// here is still answered (the Failure message), and every line said there counts
// against the user's own input with the game's limiter (GAME_MECHANICS "Command
// rate limit"). So the first answer is given, as on any channel, and the rest are
// bounded:
//   - one answer a minute to each speaker, whatever it is: a command's reply, a
//     timer sync's data, a "works by … only" refusal or the Failure message;
//   - AnswersPerWindow answers a minute to everyone together, so a channel full of
//     speakers cannot make the client flood either.
// An answer is everything said for one line of theirs. A timer sync's data is
// several lines to one asker and counts once, so it is never cut in the middle.
// A line of theirs whose answer would be held back is not acted on either: the
// engine asks WhyNot before it runs anything for it.
//
// The engine keeps two of these (RemoteCommandManager.OpenBroadcastAnswer): one for
// what is said to speakers who hold the permission for what they sent, one for the
// refusals to speakers who do not. The few speakers a minute are counted in each
// apart, so refusals never take a permitted speaker's place. The one answer a
// minute to a speaker is across both: answered in either, they wait the minute.
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

    // The speakers answered inside the minute before now: the turns spent. Counts
    // without dropping anyone, so a bug report reading it changes nothing.
    public int SpentAt(DateTimeOffset now) => _answeredAt.Values.Count(at => now - at < Window);

    // Why speaker would not be answered now, for the log, or null when they would.
    public string? WhyNot(string speaker, DateTimeOffset now)
    {
        if (AnsweredLately(speaker, now)) return AnsweredLatelyReason;
        if (_answeredAt.Count >= AnswersPerWindow)
            return $"{AnswersPerWindow} answers were given there in the last minute";
        return null;
    }

    public const string AnsweredLatelyReason = "they were answered there less than a minute ago";

    // Whether speaker was answered inside the last minute. The engine asks this of
    // both of its bounds: a speaker has one answer a minute, of whichever kind.
    public bool AnsweredLately(string speaker, DateTimeOffset now)
    {
        foreach (string stale in _answeredAt.Where(p => now - p.Value >= Window).Select(p => p.Key).ToList())
            _answeredAt.Remove(stale);
        return _answeredAt.ContainsKey(speaker);
    }

    // An answer to speaker begins now. Only after WhyNot said they may have one.
    public void Note(string speaker, DateTimeOffset now) => _answeredAt[speaker] = now;
}
