namespace MudPlay.Game.Map;

// Terminal outcome of a DoorOpenManager request. Opened means the walker
// can now safely send the cardinal move command; Failed carries a
// single-line reason the walker surfaces in its Failed event and the
// system log; NotHere means the request was aimed at the wrong room.
public abstract record DoorOpenResult
{
    // Door is open and ready for the cardinal move.
    public sealed record Opened : DoorOpenResult
    {
        public static readonly Opened Instance = new();
        private Opened() { }
    }

    // Door couldn't be opened — bash exhausted, pick exhausted, key
    // required but unavailable, or an unknown server reply broke the FSM.
    // Reason is a short user-facing failure detail. Unopenable says the door itself
    // beat this character (no verb it has can open it, or the ones it has ran out),
    // as opposed to the request breaking (stopped, a reply lost, a key missing): the
    // caller goes round such a door when it can, and trying it again would end the
    // same way.
    public sealed record Failed(string Reason, bool Unopenable = false) : DoorOpenResult;

    // The door isn't in the room the character is standing in: the game answered
    // the verb with "Your command had no effect." (the exit that way is no door
    // here), or the tracker confirmed another room while the request ran. Not a
    // failure of the door — the caller re-checks where it is and re-plans.
    public sealed record NotHere(string Reason) : DoorOpenResult;
}
