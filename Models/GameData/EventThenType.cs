namespace MudPlay.Models.GameData;

// What a ScheduledEvent does once its action is done:
//   Nothing  — stop there.
//   Resume   — go back to the loop / auto-lair / walk that was running when the
//              event (or the chain of events it belongs to) fired.
//   Loop / AutoLair / WalkTo — start that saved loop / setup, or walk there.
//   Event    — fire another event by name; its own Then carries on from there.
public enum EventThenType
{
    Nothing = 0,
    Resume = 1,
    Loop = 2,
    AutoLair = 3,
    WalkTo = 4,
    Event = 5,
}
