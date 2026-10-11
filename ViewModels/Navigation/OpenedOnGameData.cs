namespace MudPlay.ViewModels.Navigation;

// A window that edits rooms is modeless and stays open through a switch of game data.
// What it holds is the rooms of the set it was opened on: saved under another set it
// would land in that set's Loops folder, a file every character and running client on
// that set reads, and a room taken in would be the other set's. The window remembers
// the set its catalogue was on when it opened and asks here before each such step.
public static class OpenedOnGameData
{
    // Why what the window holds (a "loop", a "setup") can't be saved or added to right
    // now, or null: another game-data set is active than the one it was opened on.
    // Sets are told apart by name, so one read in again under its name is the same set.
    public static string? Refusal(string what, string? openedOn, string? active) =>
        string.Equals(active, openedOn, StringComparison.OrdinalIgnoreCase) ? null
        : $"This {what}'s rooms belong to game data '{openedOn ?? "(none)"}', and '{active ?? "(none)"}' is active now — " +
          "switch back to that game data to change or save it.";

    // The same reason, as the tail of a program-log line.
    public static string LogReason(string? openedOn, string? active) =>
        $"its rooms are game data '{openedOn ?? "(none)"}''s and '{active ?? "(none)"}' is active now";
}
