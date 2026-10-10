namespace MudPlay.Game.Health;

// What came of a run HealthManager was asked to make from a monster seen in the
// room. The first two are a run under way; the rest say why none was started.
public enum FleeOutcome
{
    // The retreat is under way, or waits for the move in flight to land first.
    Started,

    // A flee was already under way (low HP, hit and run, a player), so nothing
    // more was sent: one retreat at a time.
    AlreadyRunning,

    // Auto-Heal and Auto-Rest are both off: the health engine that owns every
    // flee is not running.
    EngineOff,

    // The character is at 0 HP or below and cannot move.
    Down,

    // A hang-up or a wimpy jump went out a moment ago. A move sent behind it
    // would go into a closing line or be walked from the wimpy location.
    Escaping,

    // The character follows a party leader, and a follower does not run off alone.
    Follower,

    // No walk or loop is running, so there is no route to retreat along.
    NoEngine,

    // A walk or loop is running but no way out of the room could be worked out.
    NoRoute,
}
