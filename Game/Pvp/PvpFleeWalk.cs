using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Pvp;

// The PvP flee to a chosen room: whatever walk, loop or lair run was under way is
// stopped, the walker heads for the room in Sprint Mode (no stopping to fight, loot
// or rest on the way), stays there, and for "come back later" the stopped run is
// picked up again once the wait, counted from arriving, is over. Stop-and-restart
// rather than a movement gate, as for the errand detours: a gate would hold the
// flee itself.
public sealed class PvpFleeWalk : IDisposable
{
    public const string LogCategory = "PvP";

    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loops;
    private readonly AutoLairManager _lair;
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly Action _startSprint;
    private readonly LogService? _log;

    private RoomKey _destination;
    private DetourResume _resume;
    private TimeSpan? _comeBackAfter;
    private int _run;

    // True from the flee starting until the walker arrives, fails or is stopped.
    public bool IsActive { get; private set; }

    // startSprint turns Sprint Mode on for the walk; like any Sprint start it ends
    // by itself when the walk arrives.
    public PvpFleeWalk(
        AutoWalkManager walker, LoopRunner loops, AutoLairManager lair,
        Action<TimeSpan, Action> schedule, Action startSprint, LogService? log = null)
    {
        _walker = walker;
        _loops = loops;
        _lair = lair;
        _schedule = schedule;
        _startSprint = startSprint;
        _log = log;
        _walker.Event += OnWalkEvent;
    }

    // comeBackAfter null leaves the stopped run stopped (a hang-up follows).
    public bool Start(RoomKey destination, TimeSpan? comeBackAfter, string reason)
    {
        if (IsActive) return true;

        DetourResume resume = DetourResume.Snapshot(_walker, _loops, _lair, includeWalk: true);
        resume.Stop(_walker, _loops, _lair, $"PvP flee: {reason}");

        _destination = destination;
        _resume = resume;
        _comeBackAfter = comeBackAfter;
        _run++;
        IsActive = true;
        if (_walker.WalkTo(destination, planThroughAcquirableGates: true))
        {
            _startSprint();
            _log?.Warn(LogCategory,
                $"fleeing to {destination} ({reason}); interrupted: {resume.Kind}");
            return true;
        }

        IsActive = false;
        _log?.Warn(LogCategory, $"no route to the flee room {destination} ({reason})");
        // The run we stopped for a flee that never started goes straight back on.
        resume.Resume(_walker, _loops, _lair);
        return false;
    }

    private void OnWalkEvent(WalkEvent e)
    {
        if (!IsActive || e.Destination is not { } dest || !dest.Equals(_destination)) return;
        switch (e.Kind)
        {
            case WalkEventKind.Finished:
                IsActive = false;
                ComeBackLater();
                break;
            case WalkEventKind.Failed:
            case WalkEventKind.Stopped:
                IsActive = false;
                _log?.Info(LogCategory, $"flee walk ended early ({e.Kind}: {e.Detail}); nothing is resumed");
                break;
        }
    }

    private void ComeBackLater()
    {
        if (_comeBackAfter is not { } wait || _resume.Kind == DetourResumeKind.None)
        {
            _log?.Info(LogCategory, $"reached the flee room {_destination}");
            return;
        }

        _log?.Info(LogCategory,
            $"reached the flee room {_destination}; picking the {_resume.Kind} back up in {wait.TotalSeconds:0}s");
        int run = _run;
        DetourResume resume = _resume;
        _schedule(wait, () =>
        {
            // Another flee, or a run the user started meanwhile, supersedes this one.
            if (run != _run || IsActive) return;
            if (DetourResume.Snapshot(_walker, _loops, _lair, includeWalk: true).Kind != DetourResumeKind.None)
                return;
            _log?.Info(LogCategory, $"coming back from the flee: resuming the {resume.Kind}");
            resume.Resume(_walker, _loops, _lair);
        });
    }

    // The user stopped while the come-back was waiting out its delay in the flee
    // room. The walker is idle then and raises nothing, so the stop is told here:
    // the walk or run fled from is not picked back up.
    public void CancelComeBack() => _run++;

    public void Dispose() => _walker.Event -= OnWalkEvent;
}
