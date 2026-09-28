using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Sends `who` to verify our alignment when a gear set disagrees with the one on
// record — an item blocked on alignment alone, or alignment-gated gear with no
// alignment known yet. The `who` line rewrites our row in the realm's players list
// and the Equipment Manager re-evaluates its blocks against it.
//
// Alignment moves during a session (Paradigm drifts toward good unless the player
// blocks it; attacking good monsters moves either realm toward evil), so this isn't
// a once-per-session check. New evidence — a new block, edited sets, a dark cloud
// that took us out of Good, a fresh session — re-checks after a short gap; a mismatch that a `who`
// already confirmed is only re-checked on a slow cadence (when a set is applied,
// and on Paradigm on a timer), so a genuinely off-alignment set doesn't spam `who`.
//
// Checks only go out on a prompt, so nothing is sent before we're in the game.
public sealed class AlignmentGearCheck
{
    private static readonly TimeSpan UrgentGap = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RoutineGap = TimeSpan.FromMinutes(10);

    private readonly Func<bool> _needsCheck;
    private readonly Func<bool> _driftsDuringPlay;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // When a pending check was last evaluated (sent or found unneeded); both gaps
    // count from here, so the realm timer doesn't rescan the sets on every prompt.
    private DateTimeOffset _lastChecked = DateTimeOffset.MinValue;
    private TimeSpan? _pendingGap;
    // The game told us our alignment moved (gear stripped, a forgive): verify even
    // when the sets agree with the old record, since the record is what's stale.
    private bool _verifyPending;

    // needsCheck: a set disagrees with our recorded alignment. driftsDuringPlay: the
    // realm moves alignment toward good while playing (Paradigm), so a confirmed
    // mismatch is worth re-checking on a timer too.
    public AlignmentGearCheck(Func<bool> needsCheck, Func<bool> driftsDuringPlay,
        LogService? log = null, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(needsCheck);
        ArgumentNullException.ThrowIfNull(driftsDuringPlay);
        _needsCheck = needsCheck;
        _driftsDuringPlay = driftsDuringPlay;
        _log = log;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // Something new may have changed the answer: a block raised or lifted, sets
    // edited, a dark cloud that took us out of Good, a new session.
    public void RequestCheck() => Arm(UrgentGap);

    // The game says our alignment moved: check it, sets or no sets.
    public void RequestVerify()
    {
        _verifyPending = true;
        Arm(UrgentGap);
    }

    // The same set was applied again: worth another look, but not often.
    public void RequestRoutineCheck() => Arm(RoutineGap);

    private void Arm(TimeSpan gap)
    {
        if (_pendingGap is not { } current || gap < current) _pendingGap = gap;
    }

    // Every prompt. Sends the pending check once its gap has passed, and on a realm
    // whose alignment drifts during play, re-arms a routine check on its own.
    public void OnPrompt()
    {
        DateTimeOffset now = _now();
        if (_pendingGap is null && now - _lastChecked >= RoutineGap && _driftsDuringPlay())
            _pendingGap = RoutineGap;
        if (_pendingGap is not { } gap || now - _lastChecked < gap) return;
        _pendingGap = null;
        _lastChecked = now;
        bool verify = _verifyPending;
        _verifyPending = false;
        if (!verify && !_needsCheck()) return;
        _wire.Send("who");
        _log?.Info("Equipment", verify
            ? "the game says our alignment moved — sent `who` to learn where it is"
            : "gear set disagrees with our recorded alignment — sent `who` to verify it");
    }
}
