namespace MudPlay.Game;

// What PartyPoller.RequestParUnlessPending did with a request for a `par`.
public enum ParRequestOutcome
{
    // A `par` went out for it.
    Sent,
    // A `par` already sent hasn't been answered yet; its reply answers this too.
    OnItsWay,
    // The timer's next `par` is due within PartyPoller.ParDueWindow.
    DueSoon,
    // Nothing could be sent: no party, a disconnect, the trainer menu, the master
    // switch. The next `par` read answers it.
    Held,
}
