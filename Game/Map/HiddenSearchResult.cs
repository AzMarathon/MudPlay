namespace MudPlay.Game.Map;

// Terminal outcome of a HiddenExitRevealManager request. Revealed means
// the hidden exit now appears in the tracker's current room and the
// walker can send the cardinal move; Failed carries a single-line reason
// (search attempts exhausted, exit never revealed, etc.).
public abstract record HiddenSearchResult
{
    // Hidden exit is now visible — walker can proceed with the cardinal step.
    public sealed record Revealed : HiddenSearchResult
    {
        public static readonly Revealed Instance = new();
        private Revealed() { }
    }

    // Exit couldn't be revealed within the configured cap.
    public sealed record Failed(string Reason) : HiddenSearchResult;

    // The tracker confirmed a DIFFERENT room mid-search — a move already on the wire
    // (queued behind other commands) landed, so every further `sea` would search the
    // wrong room. Not a failure of the exit: the walker re-plans from where it is.
    public sealed record LeftRoom(RoomKey SearchedIn, RoomKey NowIn) : HiddenSearchResult;
}
