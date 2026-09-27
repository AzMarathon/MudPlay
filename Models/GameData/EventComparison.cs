namespace MudPlay.Models.GameData;

// How an EventCondition compares the stat with its value.
public enum EventComparison
{
    AtLeast = 0,   // >=
    AtMost = 1,    // <=
    Above = 2,     // >
    Below = 3,     // <
    Equal = 4,     // =
    NotEqual = 5,  // !=
}
