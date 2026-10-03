using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Remote;

// Recognises another MudPlay user's @path reply, or our party leader's reply to an
// @goto they accepted, and announces what it reports, so the navigation map can draw
// the route they're walking. A reply travels back on whatever
// channel the @path went out on, so this listens on telepath, gangpath and say
// (including a directed say — "Nineteen says (to you) "{walking to 6/1249; …}"").
// Our own "You say" echo has no speaker and is ignored.
//
// Fire-and-forget like WhereReplyTracker: it only raises PathReported, and the nav map
// decides whether to act (it draws only while its window is open). Nothing is sent —
// the route is built from the reply alone.
public sealed class PathReplyTracker : IDisposable
{
    public const string LogCategory = "PathReply";

    // The responder's given name + what their reply reported.
    public event Action<string, PathReport>? PathReported;

    // Our party leader's given name + where their accepted @goto is taking them.
    // That reply carries no room or step count, so it's only taken from the leader:
    // a follower walks with them, so their route starts where we stand.
    public event Action<string, RoomKey>? GotoReported;

    // The responder's given name, when their @path reply says they aren't moving.
    public event Action<string>? IdleReported;

    // The most recent reply, for the bug report — a "the leader's route looks wrong"
    // report needs exactly what we were told.
    public (string Sender, PathReport Report, DateTimeOffset At)? Last { get; private set; }

    public (string Sender, RoomKey Destination, DateTimeOffset At)? LastGoto { get; private set; }

    private readonly List<IDisposable> _subs = new();
    private readonly LogService? _log;
    private readonly Func<string, bool> _isPartyLeader;
    private bool _disposed;

    public PathReplyTracker(MessageRouter router, Func<string, bool>? isPartyLeader = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _isPartyLeader = isPartyLeader ?? (_ => false);
        _log = log;
        // Telepath / gangpath: group 0 = sender, 1 = body.
        _subs.Add(router.Subscribe(KnownPatterns.ConversationTelepathIn, r => OnReply(r, 0, 1)));
        _subs.Add(router.Subscribe(KnownPatterns.ConversationGangpath, r => OnReply(r, 0, 1)));
        // Say: group 0 = speaker (empty for our own), 1 = directed target, 2 = text.
        _subs.Add(router.Subscribe(KnownPatterns.ConversationLocal, r => OnReply(r, 0, 2)));
    }

    private void OnReply(MatchResult result, int senderGroup, int bodyGroup)
    {
        if (result.Groups.Count <= bodyGroup) return;
        string sender = result.Groups[senderGroup].Trim();
        if (sender.Length == 0) return;
        string body = result.Groups[bodyGroup];
        if (PathReplyParser.TryParseGoto(body, out RoomKey dest))
        {
            if (!_isPartyLeader(sender)) return;
            _log?.Info(LogCategory, $"@goto reply from party leader {sender}: walking to {dest}");
            LastGoto = (sender, dest, DateTimeOffset.UtcNow);
            GotoReported?.Invoke(sender, dest);
            return;
        }
        if (PathReplyParser.IsIdleReply(body))
        {
            IdleReported?.Invoke(sender);
            return;
        }
        if (!PathReplyParser.TryParse(body, out PathReport? report) || report is null) return;
        _log?.Info(LogCategory,
            $"@path reply from {sender}: at {report.LeaderRoom}, " +
            (report.Destination is { } d ? $"walking to {d}" : report.LoopName is { } l ? $"loop '{l}'" : "no destination") +
            $", step {report.Step}/{report.TotalSteps}");
        Last = (sender, report, DateTimeOffset.UtcNow);
        PathReported?.Invoke(sender, report);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IDisposable s in _subs) s.Dispose();
    }
}
