namespace MudPlay.Models.GameData;

// Which moment on a boss's timer fires a Boss-triggered event (the Bosses tab's
// timer table):
//   EarlyWindow  — an early spawn window's column counts down to 0
//                  (ScheduledEvent.BossWindowFraction: Paradigm -20% / -10% / -5%,
//                  Stock 87.5%).
//   Guaranteed   — its full respawn time is up.
//   Killed       — it dies.
//   CleanupReset — a cleanup-respawn boss flips back to alive at nightly cleanup.
public enum EventBossMoment
{
    EarlyWindow = 0,
    Guaranteed = 1,
    Killed = 2,
    CleanupReset = 3,
}
