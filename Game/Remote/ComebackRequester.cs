using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Remote;

// Follower-side @comeback sender. When the party leader walks off and a
// movement-blocking condition leaves us behind, this telepaths
// @comeback <map>/<room> (or a bare @comeback) to the leader so their
// PartyComebackManager recovers us.
//
// Disambiguating "left behind" from a deliberate unfollow. The game prints
// "You are no longer following X." for three distinct situations: the leader
// uninvited us, we issued our own unfollow, or we genuinely couldn't keep up.
// Only the third warrants an automatic @comeback. The tell is a movement-failure
// line fired the instant before — "You can't seem to move anywhere!" (a
// prevents-movement gamedata flag) or "...too heavy to move" (over-encumbered) —
// OR a live movement-preventing affliction (knocked down / held / stunned) at the
// moment the break lands: the leader left before it cleared. The knockdown refusal
// ("You are flat on your back!") isn't one of the two failure patterns above, so
// without the condition check a break while knocked down never auto-@comeback'd
// (report paradigm-20260922-085609). If any of those held inside LeftBehindWindow of
// the "no longer following" line, we were stranded; otherwise it was deliberate and
// we stay quiet.
//
// The leader's name comes from the "no longer following" line's capture group
// (already a given/first name — the \w+ pattern never spans a space). The room
// comes from RoomTracker when its confidence is Confirmed; otherwise we send a
// bare @comeback and let the leader backtrack to find us.
public sealed class ComebackRequester : IDisposable
{
    private const string LogCategory = "Comeback";

    // Maximum gap between a movement-failure line and the following
    // "You are no longer following X." for the pair to count as a genuine
    // left-behind. A deliberate uninvite/unfollow has no preceding failure, so
    // its gap is effectively infinite.
    private static readonly TimeSpan LeftBehindWindow = TimeSpan.FromSeconds(3);

    private readonly RoomTracker _tracker;
    private readonly LogService? _log;
    private readonly Func<bool>? _isMovementPrevented;
    private readonly List<IDisposable> _subs = new();

    private Action<byte[]>? _wireSender;
    private DateTimeOffset _moveFailedAt = DateTimeOffset.MinValue;
    private bool _disposed;

    // Test seam for the clock so the LeftBehindWindow gate is deterministic.
    internal Func<DateTimeOffset> NowProvider { get; set; } = static () => DateTimeOffset.Now;

    // Mirrors OtherSettings.AutoRequestComebackWhenLeftBehind. When false,
    // left-behind detection still runs but no @comeback is sent.
    public bool Enabled { get; set; } = true;

    // Test-visible record of every wire payload sent.
    internal List<byte[]> LastSentForTests { get; } = new();

    // isMovementPrevented reports whether a movement-blocking affliction (knockdown /
    // held / stun) is active right now — a second left-behind tell alongside the two
    // movement-failure lines.
    public ComebackRequester(MessageRouter router, RoomTracker tracker, LogService? log = null,
        Func<bool>? isMovementPrevented = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
        _log = log;
        _isMovementPrevented = isMovementPrevented;

        _subs.Add(router.Subscribe(KnownPatterns.MovementFailedStuck, OnMovementFailed));
        _subs.Add(router.Subscribe(KnownPatterns.MovementFailedHeavy, OnMovementFailed));
        _subs.Add(router.Subscribe(KnownPatterns.PartyYouNoLongerFollowing, OnNoLongerFollowing));
    }

    // Bind the outbound wire — the same TelnetClient.SendAsync wrapper the other
    // engines use.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
    }

    private void OnMovementFailed(MatchResult _) => _moveFailedAt = NowProvider();

    private void OnNoLongerFollowing(MatchResult result)
    {
        DateTimeOffset now = NowProvider();
        bool recentFailure = now - _moveFailedAt <= LeftBehindWindow;
        bool movementPrevented = _isMovementPrevented?.Invoke() == true;
        if (!recentFailure && !movementPrevented)
            return; // deliberate uninvite / our own unfollow — stay quiet
        _moveFailedAt = DateTimeOffset.MinValue; // consume the failure so it can't arm a later line

        string leaderGiven = result.Groups.Count > 0 ? result.Groups[0].Trim() : string.Empty;
        if (string.IsNullOrEmpty(leaderGiven))
            return;

        if (!Enabled)
        {
            _log?.Debug(LogCategory, $"left behind by {leaderGiven} but auto-@comeback disabled");
            return;
        }

        // Only attach a room when we're confident where we are; a stale
        // guess would send the leader to the wrong place. Bare @comeback
        // makes the leader backtrack the path they just walked.
        string payload = "@comeback";
        if (_tracker.State.Confidence == RoomConfidence.Confirmed
            && _tracker.State.CurrentRoom is { } room)
            payload = $"@comeback {room.Key}";

        string wire = $"/{leaderGiven} {payload}";
        byte[] bytes = Encoding.Latin1.GetBytes(wire + "\r");
        LastSentForTests.Add(bytes);
        _wireSender?.Invoke(bytes);
        _log?.Info(LogCategory, $"left behind by {leaderGiven} — sent {payload}");
    }
}
