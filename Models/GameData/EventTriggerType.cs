namespace MudPlay.Models.GameData;

// When a ScheduledEvent fires.
//   Logon — fires on every successful game entry (every
//     LoginAutomator.LoggedIntoGame). Includes first-connect and every
//     subsequent reconnect.
//   Logoff — fires once during a clean shutdown, before the wire closes.
//     Dropped connections skip Logoff events.
//   Relog — fires alongside Logon, but ONLY when the game entry is a
//     reconnect (a prior in-session disconnect happened). Never fires on
//     the first game entry of a program run.
//   AtTime — fires at the configured HH:mm wall-clock local time. Only
//     fires while connected + in-game; no catch-up after a missed window.
//   Every — recurring cadence based on ScheduledEvent.EveryAmount and
//     ScheduledEvent.EveryUnit. Timer pauses on disconnect and restarts
//     from zero on reconnect (no anchor preservation).
//   State — fires when every one of ScheduledEvent.Conditions becomes true
//     (money / encumbrance / experience / level against a value). Once per
//     crossing: it re-arms only after the conditions stop holding. Only fires
//     while in-game.
//   Boss — a moment on a boss's timer (ScheduledEvent.BossName +
//     BossMoment, optionally BossLeadMinutes early): its first early spawn
//     window, its guaranteed spawn, its kill, or a cleanup boss's reset. Once
//     per kill. Only fires while in-game.
public enum EventTriggerType
{
    Logon = 0,
    Logoff = 1,
    Relog = 2,
    AtTime = 3,
    Every = 4,
    State = 5,
    Boss = 6,
}
