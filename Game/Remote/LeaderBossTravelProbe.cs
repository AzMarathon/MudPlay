using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Follower side of the Bossing set's "keep on between bosses" option. A follower is
// carried along by the game's follow and has no route of its own, so the only way
// to know whether the party is heading for another boss is to ask the leader's
// client: this telepaths it @path and reports whether what it says it's doing ends
// at a boss room.
//
// The answer goes stale (the leader can pick somewhere else), so while the gear is
// still being kept on it asks again every RecheckInterval. No reply inside
// ReplyTimeout (the leader isn't running MudPlay) counts as no.
//
// A leader that answers "not moving" hasn't picked where to go yet: nothing is
// decided until the party next moves (NoteMoved), when it is asked once more and
// that answer stands. Idle a second time means the leader is walking by hand.
public sealed class LeaderBossTravelProbe : IDisposable
{
    public const string LogCategory = "BossTravel";

    internal static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);

    private readonly PathReplyTracker _replies;
    private readonly Func<string?> _leaderGivenName;
    private readonly Func<PathReport, bool> _headsToBoss;
    private readonly Func<RoomKey, bool> _isBossRoom;
    private readonly Func<bool> _stillNeeded;
    private readonly Func<TimeSpan, Action, IDisposable>? _schedule;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private IDisposable? _timer;
    private bool _awaiting;
    // The leader said it wasn't moving; ask again when the party moves.
    private bool _waitingForLeaderToMove;
    // The question out is that second ask, so an idle answer to it is final.
    private bool _confirming;
    private bool _disposed;

    public bool WaitingForLeaderToMove => _waitingForLeaderToMove;

    // Whether the leader's trip ends at a boss room. Raised once per question.
    public event Action<bool>? Resolved;

    // The last question's outcome, for the bug report.
    public string LastOutcome { get; private set; } = "(never asked)";

    // Test-visible record of every wire payload sent.
    internal List<byte[]> LastSentForTests { get; } = new();

    // leaderGivenName: the leader we're following, or null when we lead / are solo.
    // headsToBoss: whether a leader's @path report describes a trip to a boss room.
    // stillNeeded: the gear is still being kept on, so the answer is worth refreshing.
    // schedule: one-shot timer (null in tests, which drive Timeout / Recheck directly).
    public LeaderBossTravelProbe(
        PathReplyTracker replies,
        Func<string?> leaderGivenName,
        Func<PathReport, bool> headsToBoss,
        Func<RoomKey, bool> isBossRoom,
        Func<bool> stillNeeded,
        Func<TimeSpan, Action, IDisposable>? schedule = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(replies);
        ArgumentNullException.ThrowIfNull(leaderGivenName);
        ArgumentNullException.ThrowIfNull(headsToBoss);
        ArgumentNullException.ThrowIfNull(isBossRoom);
        ArgumentNullException.ThrowIfNull(stillNeeded);
        _replies = replies;
        _leaderGivenName = leaderGivenName;
        _headsToBoss = headsToBoss;
        _isBossRoom = isBossRoom;
        _stillNeeded = stillNeeded;
        _schedule = schedule;
        _log = log;
        _replies.PathReported += OnPathReported;
        _replies.GotoReported += OnGotoReported;
        _replies.IdleReported += OnIdleReported;
    }

    public void SetWireSender(Action<byte[]> sender) => _wireSender = sender;

    // True when there's a leader to ask.
    public bool HasLeader => _leaderGivenName() is { Length: > 0 };

    // Ask the leader where it's going. False when there's nobody to ask. A question
    // already out is left to run.
    public bool Ask()
    {
        if (_disposed || _leaderGivenName() is not { Length: > 0 } leader) return false;
        if (_awaiting) return true;
        _awaiting = true;
        byte[] bytes = Encoding.Latin1.GetBytes($"/{leader} @path\r");
        LastSentForTests.Add(bytes);
        _wireSender?.Invoke(bytes);
        _log?.Info(LogCategory, $"asked {leader} where the party is heading (@path)");
        Arm(ReplyTimeout, OnTimeout);
        return true;
    }

    private void OnPathReported(string sender, PathReport report)
    {
        if (!IsLeader(sender) || !(_awaiting || _stillNeeded())) return;
        Resolve(_headsToBoss(report), report.Destination is { } d ? $"{sender} is walking to {d}"
            : report.LoopName is { } l ? $"{sender} is on loop '{l}' (only a walk-to keeps the set on)"
            : $"{sender} reported no destination");
    }

    // The leader accepted an @goto: that's its new destination, asked for or not.
    private void OnGotoReported(string sender, RoomKey destination)
    {
        if (!IsLeader(sender) || !(_awaiting || _stillNeeded())) return;
        Resolve(_isBossRoom(destination), $"{sender} accepted a walk to {destination}");
    }

    private void OnIdleReported(string sender)
    {
        if (!IsLeader(sender) || !_awaiting) return;
        if (_confirming)
        {
            Resolve(false, $"{sender} is moving without a walk-to");
            return;
        }
        _awaiting = false;
        CancelTimer();
        _waitingForLeaderToMove = true;
        LastOutcome = $"{sender} isn't moving yet — asking again when the party moves";
        _log?.Info(LogCategory, LastOutcome);
    }

    // We changed rooms. Following, that means the leader moved: if it was idle when
    // asked, this is the moment to ask where it's going.
    public void NoteMoved()
    {
        if (!_waitingForLeaderToMove) return;
        _waitingForLeaderToMove = false;
        if (!_stillNeeded()) return;
        _confirming = true;
        if (!Ask()) _confirming = false;
    }

    private bool IsLeader(string sender) =>
        _leaderGivenName() is { Length: > 0 } leader
        && string.Equals(leader, sender, StringComparison.OrdinalIgnoreCase);

    internal void OnTimeout()
    {
        if (!_awaiting) return;
        Resolve(false, $"no @path answer in {ReplyTimeout.TotalSeconds:0}s");
    }

    internal void OnRecheck()
    {
        _timer = null;
        if (_stillNeeded()) Ask();
    }

    private void Resolve(bool headingToBoss, string what)
    {
        _awaiting = false;
        _confirming = false;
        _waitingForLeaderToMove = false;
        CancelTimer();
        LastOutcome = $"{what} — {(headingToBoss ? "a boss room" : "not a boss room")}";
        _log?.Info(LogCategory, LastOutcome);
        Resolved?.Invoke(headingToBoss);
        // Keep the answer fresh for as long as the gear rides on it.
        if (headingToBoss && _stillNeeded()) Arm(RecheckInterval, OnRecheck);
    }

    private void Arm(TimeSpan delay, Action action)
    {
        CancelTimer();
        _timer = _schedule?.Invoke(delay, action);
    }

    private void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelTimer();
        _replies.PathReported -= OnPathReported;
        _replies.GotoReported -= OnGotoReported;
        _replies.IdleReported -= OnIdleReported;
    }
}
