namespace MudPlay.ViewModels.Navigation;

// The game-data set a window's rooms are of: the one its catalogue was on when it
// opened, or the one a running loop or a set of lair marks was stamped with. A window
// that edits rooms is modeless and stays open through a switch of game data, and a
// loop or marks from before one can be put back afterwards. Saved under another
// set, what they hold would land in that set's Loops folder, a file every character
// and running client on that set reads, and a room taken in would be the other
// set's: each asks here before such a step.
//
// Set is null for "no game data was active", which is a set like any other here. A
// caller with no stamp to give passes no OpenedOnGameData at all, never one of null.
public readonly record struct OpenedOnGameData(string? Set)
{
    // Why what is held (a "loop", a "setup") can't be saved or added to right now, or
    // null: another game-data set is active than the one its rooms are of. Sets are
    // told apart by name, so one read in again under its name is the same set.
    public string? Refusal(string what, string? active) =>
        string.Equals(active, Set, StringComparison.OrdinalIgnoreCase) ? null
        : $"This {what}'s rooms belong to game data '{Set ?? "(none)"}', and '{active ?? "(none)"}' is active now — " +
          "switch back to that game data to change or save it.";

    // The same reason, as the tail of a program-log line.
    public string LogReason(string? active) =>
        $"its rooms are game data '{Set ?? "(none)"}''s and '{active ?? "(none)"}' is active now";
}
