namespace MudPlay.Game;

// What a finished trap request means for the exit it was asked about.
public enum TrapReplyOutcome
{
    // Disarmed, already down, or no trap that way: the exit can be taken.
    Clear,
    // Every disarm set the trap off: it is still armed.
    Failed,
    // The request was cancelled before it finished.
    Stopped,
    // Asked of the party, and nobody took it.
    Unanswered,
}
