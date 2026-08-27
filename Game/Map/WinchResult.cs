namespace MudPlay.Game.Map;

// Terminal outcome of a WinchManager request. Turned means the gate the winch
// controls is now open and the caller can safely send the cardinal move; Failed
// carries a single-line reason the engine surfaces in its Failed event + log.
public abstract record WinchResult
{
    // The winch turned and the gate it controls now reads open — ready for the move.
    public sealed record Turned : WinchResult
    {
        public static readonly Turned Instance = new();
        private Turned() { }
    }

    // The winch couldn't be turned (retries exhausted with no success), or the gate
    // never opened after it turned, or an unknown reply wedged the FSM.
    public sealed record Failed(string Reason) : WinchResult;
}
