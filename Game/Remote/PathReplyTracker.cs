using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Remote;

// Recognises another MudPlay user's @path reply and announces what it reports, so the
// navigation map can draw the route they're walking. A reply travels back on whatever
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

    // The most recent reply, for the bug report — a "the leader's route looks wrong"
    // report needs exactly what we were told.
    public (string Sender, PathReport Report, DateTimeOffset At)? Last { get; private set; }

    private readonly List<IDisposable> _subs = new();
    private readonly LogService? _log;
    private bool _disposed;

    public PathReplyTracker(MessageRouter router, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
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
        if (!PathReplyParser.TryParse(result.Groups[bodyGroup], out PathReport? report) || report is null) return;
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
