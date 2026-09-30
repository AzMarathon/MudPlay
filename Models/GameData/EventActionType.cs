namespace MudPlay.Models.GameData;

// What a ScheduledEvent does when its trigger fires. One action per event.
//   WalkTo — start a walk to ScheduledEvent.WalkToTarget via
//     AutoWalkManager. Stops any running loop / auto-lair first
//     (supersede semantics — same as the @goto remote command).
//   Loop — start the saved loop named ScheduledEvent.LoopName via
//     LoopRunner.Start. Stops auto-lair + walker first.
//   AutoLair — start the saved auto-lair setup named
//     ScheduledEvent.AutoLairSetupName via AutoLairManager.Start.
//     Stops loop + walker first.
//   Command — send ScheduledEvent.CommandText on the wire. Supports
//     multi-fire via ^M and ; separators; each chunk is sent as its own
//     CR-terminated line. Matches the splitter the Macro / Trigger /
//     Alias command surfaces already use.
//   Roomba — start a Roomba sweep in ScheduledEvent.RoombaMode (Sort or
//     Inventory only). Stops any walk / loop / auto-lair first; the sweep
//     drives the loop runner itself.
//   Wait — stop moving and stand still for ScheduledEvent.WaitSeconds.
//   RestUp — rest / meditate to the Health tab's rest max, as a loop room
//     flagged "rest up here" does.
//   BankTrip — walk to the Settings → Cash bank or stash room and deposit /
//     stash there (AutoDepositManager.StartEventTrip).
public enum EventActionType
{
    WalkTo = 0,
    Loop = 1,
    AutoLair = 2,
    Command = 3,
    Roomba = 4,
    Wait = 5,
    RestUp = 6,
    BankTrip = 7,
}
