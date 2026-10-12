using MudPlay.Services;

namespace MudPlay.Game.Map;

// The loop, Auto-Lair or walk an interruption stood down to put back when it is
// over, kept where it can be asked about and let go. A fight with a player is the
// one: it stops the engines for as long as it lasts (it may walk, in a chase, which
// a movement gate would hold) and restarts what it stopped at its end.
//
// Every engine is idle meanwhile, so a stop finds nothing running. A change of
// game data asks here instead (MovementController.AddHeldStart) and drops the way
// back: the loop's rooms, the lair marks and the walk's destination are numbers of
// the old data, and the fight's end would walk them on the new rooms.
public sealed class HeldDetourResume
{
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loops;
    private readonly AutoLairManager _lair;
    private readonly string _heldFor;
    private readonly Func<bool> _stillWaiting;
    private readonly LogService? _log;

    private DetourResume _resume;
    // The suspend whose way back is still owed; null once it was taken or dropped.
    private object? _owed;

    // heldFor is what holds it, as it reads in a sentence ("a fight"). stillWaiting
    // answers whether the interruption is still on: one that ended without taking
    // its way back (the character died in the fight) holds nothing any more.
    public HeldDetourResume(
        AutoWalkManager walker, LoopRunner loops, AutoLairManager lair, string heldFor, Func<bool> stillWaiting,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lair);
        ArgumentNullException.ThrowIfNull(heldFor);
        ArgumentNullException.ThrowIfNull(stillWaiting);
        _walker = walker;
        _loops = loops;
        _lair = lair;
        _heldFor = heldFor;
        _stillWaiting = stillWaiting;
        _log = log;
    }

    // What the last suspend stopped and is still to go back to: None for one that
    // interrupted nothing, and once the way back was dropped.
    public DetourResumeKind Interrupted { get; private set; }

    // Stop what is running, and hand back the way to restart it.
    public Action Suspend(string why)
    {
        DetourResume resume = DetourResume.Snapshot(_walker, _loops, _lair, includeWalk: true);
        resume.Stop(_walker, _loops, _lair, why);
        Interrupted = resume.Kind;
        _resume = resume;
        object owed = new();
        _owed = owed;
        return () =>
        {
            if (!ReferenceEquals(_owed, owed)) return;
            _owed = null;
            resume.Resume(_walker, _loops, _lair);
        };
    }

    // What is held, as it reads in the notice of what a stop dropped ("loop 'Ring',
    // held for a fight,"); null when nothing is.
    public string? Held
    {
        get
        {
            if (_owed is null || !_stillWaiting()) return null;
            return What() is { } what ? $"{what}, held for {_heldFor}," : null;
        }
    }

    private string? What() => _resume.Kind switch
    {
        DetourResumeKind.Lair => "Auto-Lair",
        DetourResumeKind.Loop => $"loop '{_resume.Loop?.Name ?? "?"}'",
        DetourResumeKind.Walk => _resume.WalkDestination is { } to ? $"walk to {to}" : "walk",
        _ => null,
    };

    // Let the way back go: the interruption carries on, and puts nothing back.
    public void Drop(string reason)
    {
        if (_owed is null || What() is not { } what) return;
        _owed = null;
        _log?.Info("Movement", $"{what}, stopped for {_heldFor}, will not be put back when that is over: {reason}");
        Interrupted = DetourResumeKind.None;
    }
}
