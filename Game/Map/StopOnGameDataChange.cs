using MudPlay.Services;

namespace MudPlay.Game.Map;

// Game data read in afresh stops whatever is walking by the old data's room
// numbers: another set become the active one, or the active one imported again or
// rewritten in Manage Sets, which may have changed its rooms (owner, 2026-10-11).
// The Game Data menu's pick of the set already active reads it in again
// (ReloadActiveSet), so it stops like an import; only a SwitchSet to the active
// name, which the cache answers without a new load, raises nothing.
//
// This is the decision the cache's ActiveSetChanged is answered with: which of
// those the event was, told by the load it left and the one it arrived on
// (GameDataLoadChange), and the stop made for it. Whoever subscribes it does so
// ahead of the room graph's own handler, so the stop comes before the graph is
// rebuilt and nothing re-plans those numbers on the new rooms first.
public sealed class StopOnGameDataChange
{
    private readonly Func<GameDataCache.Load> _activeLoad;
    private readonly Func<MovementController?> _controller;
    private readonly Action<GameDataLoadChange>? _afterStop;
    private GameDataCache.Load _walkedOn;

    // controller answers null until the movement controller is built: before it
    // exists nothing is running, and the load is still followed.
    //
    // afterStop is run once the stop for a change has been made, whether or not it
    // found anything running: for what may only be let go with every engine at
    // rest (a hold whose release would let a walk about to be stopped take a step).
    public StopOnGameDataChange(
        Func<GameDataCache.Load> activeLoad, Func<MovementController?> controller,
        Action<GameDataLoadChange>? afterStop = null)
    {
        ArgumentNullException.ThrowIfNull(activeLoad);
        ArgumentNullException.ThrowIfNull(controller);
        _activeLoad = activeLoad;
        _controller = controller;
        _afterStop = afterStop;
        _walkedOn = activeLoad();
    }

    // What a stop ended ("Loop 'Ring' stopped") and why, for the terminal notice
    // and for whoever settles the client as the Stop button leaves it.
    public readonly record struct Outcome(string Ended, string Why)
    {
        public string Notice => $"[{Ended}: {Why}]";
    }

    // The cache raised ActiveSetChanged. Null when there is nothing to say: no
    // fresh load, no controller yet, or a stop that found nothing running or held
    // (the first set a starting client applies is one of those).
    //
    // because: what brought the change about when it was not the user changing
    // the game data (a profile closed or loaded, whose realm runs on another set),
    // so the notice and the log name that and not a change the user never made.
    public Outcome? OnActiveSetChanged(string? because = null)
    {
        GameDataCache.Load was = _walkedOn;
        _walkedOn = _activeLoad();
        GameDataLoadChange change = GameDataLoadChange.Between(was, _walkedOn);
        if (_controller() is not { } controller || !change.Changed) return null;
        string? ended = controller.StopForGameDataChange(change.Was, change.Now, change.ReadInAgain, because);
        _afterStop?.Invoke(change);
        if (ended is null) return null;
        return new Outcome(ended, because is null ? change.Why : $"{because}, and with it {change.Why}");
    }
}
