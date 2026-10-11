namespace MudPlay.Game.Remote;

// Whose doing a `@timer sync` telepath of ours was, told when its echo comes back.
//
// The merge window adopts a timer outright only from a player the USER telepathed
// the request to. Our client also telepaths that request for other players: a
// relay-back (`&@timer sync` makes us telepath the asker `@timer sync`) and a
// `@do /name @timer sync`. Its echo looks the same as the user's own, and taken as
// one it would let a player with Execute commands have their own timers written
// here with no pick.
//
// So each such send is noted as it goes out, with who it was addressed to, and the
// echo that names that player is the one it is paired with. The game acknowledges
// telepaths in send order and names the player in full even when the telepath
// abbreviated the name (GAME_MECHANICS "Telepath throttle and per-telepath
// acknowledgement"), so a note is matched when what was typed begins the name the
// echo carries. What the user types at the terminal goes out key by key and is
// never seen here as a line: an echo with no note to pair is the user's.
//
// A send the game never acknowledged (throttled, or no such player) would wait for
// an echo that is not coming and be paired with a later request of the user's to
// that player, which only makes that one a pick. It is dropped after StaleAfter.
public sealed class TimerSyncRequestOrigin(Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    // The requests this client telepathed for another player, oldest first.
    private readonly List<(string Target, DateTimeOffset At)> _sentForOthers = new();

    // The echo last judged and what was said of it: the app and an open merge
    // window both ask about the same line.
    private (ChatLogEntry Echo, bool ByUser)? _last;

    // A line is going out. usersOwn: the user typed it or set it up (a button, a
    // macro, a trigger, an event); false for one the client sends on its own or for
    // another player's remote command.
    public void ObserveOutbound(byte[] data, bool usersOwn)
    {
        if (usersOwn || data.Length == 0) return;
        string text = System.Text.Encoding.Latin1.GetString(data);
        foreach (string line in text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length < 3 || line[0] != '/') continue;
            string[] words = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 3 || TransferChannels.Identify(words[1], words[2..]) != Transfer.TimerSync) continue;
            _sentForOthers.Add((words[0], _now()));
        }
    }

    private static readonly char[] LineBreaks = { '\r', '\n' };

    // Whether the request this echo shows going out was the user's. echo is the
    // chat log's entry for a telepath of ours that was sent: its speaker is the
    // player it went to.
    public bool MadeByUser(ChatLogEntry echo)
    {
        if (_last is { } last && last.Echo == echo) return last.ByUser;
        DateTimeOffset now = _now();
        _sentForOthers.RemoveAll(s => now - s.At > StaleAfter);
        string to = echo.Speaker ?? string.Empty;
        int at = _sentForOthers.FindIndex(s => to.StartsWith(s.Target, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) _sentForOthers.RemoveAt(at);
        _last = (echo, at < 0);
        return at < 0;
    }
}
