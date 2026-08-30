using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Remote;

// Recognises an incoming MudPlay @where reply telepath and announces the room it
// reports, so the navigation map can flash the target square. When you `@where`
// another MudPlay user, their client answers with a wrapped location reply
// ("Fujin telepaths: {Adventurer's Guild, Universal Trainer (map 1, room 1376);
// exit s: west}"); WhereReplyParser pins the map/room out of it.
//
// Fire-and-forget: it just raises TargetLocated. The nav map decides whether to
// act (it only highlights while its window is open). Any telepath NOT in the
// MudPlay reply format is ignored — the parser requires the "{…(map N, room M)…}"
// wrapper, so a human mentioning a room in prose can't trip it.
public sealed class WhereReplyTracker : IDisposable
{
    public const string LogCategory = "Where";

    // The given name of the responder + the room their @where reply reported.
    public event Action<string, RoomKey>? TargetLocated;

    private readonly IDisposable _sub;
    private readonly LogService? _log;
    private bool _disposed;

    public WhereReplyTracker(MessageRouter router, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _log = log;
        _sub = router.Subscribe(KnownPatterns.ConversationTelepathIn, OnTelepathIn);
    }

    private void OnTelepathIn(MatchResult result)
    {
        // Groups: 0 = sender (given name, \w+), 1 = the telepath body.
        if (result.Groups.Count < 2) return;
        if (!WhereReplyParser.TryParseRoom(result.Groups[1], out RoomKey room)) return;
        string sender = result.Groups[0].Trim();
        _log?.Debug(LogCategory, $"@where reply from {sender} at {room.Map}/{room.Room} — flashing the map");
        TargetLocated?.Invoke(sender, room);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sub.Dispose();
    }
}
