namespace MudPlay.Game.Health;

// What came of an escape HealthManager was asked to make. The first two are an
// escape that went out; the rest say why nothing did.
public enum EscapeOutcome
{
    // The exit command went out and the carrier is being closed.
    HungUp,

    // The sysop wimpy jump went out in the hang-up's place. The session goes on.
    Jumped,

    // No exit command is set, or the carrier drop was not allowed and no wimpy
    // jump stood in for it.
    NotSent,

    // Disable Hangups is on.
    HangupsDisabled,

    // Auto-Heal and Auto-Rest are both off and Allow hangup in all-off mode is not
    // ticked: the all-off rule the low-HP hang-up follows.
    AllOff,
}
