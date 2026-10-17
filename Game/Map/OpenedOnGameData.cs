namespace MudPlay.Game.Map;

// The game-data set some rooms are of: the one a window's catalogue was on when it
// opened, the one a running loop or a set of lair marks was stamped with, or the one
// a death was recorded on. A window that edits rooms is modeless and stays open
// through a switch of game data, and a loop, marks or a death record from before one
// are still there afterwards. Saved under another set, what an editor holds would
// land in that set's Loops folder, a file every character and running client on that
// set reads; a room taken in would be the other set's, and a death room walked to
// would be another room of the same number: each asks here before such a step.
//
// Set is null for "no game data was active", which is a set like any other here. A
// caller with no stamp to give passes no OpenedOnGameData at all, never one of null.
public readonly record struct OpenedOnGameData(string? Set)
{
    // Why what is held (a "loop", a "setup", a "death") can't have toDo done to it
    // right now, or null: another game-data set is active than the one its rooms are
    // of. Sets are told apart by name, so one read in again under its name is the
    // same set.
    public string? Refusal(string what, string? active, string toDo = "change or save it") =>
        string.Equals(active, Set, StringComparison.OrdinalIgnoreCase) ? null
        : $"This {what}'s rooms belong to game data '{Set ?? "(none)"}', and '{active ?? "(none)"}' is active now — " +
          $"switch back to that game data to {toDo}.";

    // The same reason, as the tail of a program-log line.
    public string LogReason(string? active) =>
        $"its rooms are game data '{Set ?? "(none)"}''s and '{active ?? "(none)"}' is active now";
}
