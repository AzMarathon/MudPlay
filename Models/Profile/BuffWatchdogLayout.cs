namespace MudPlay.Models.Profile;

// How the Buff Watchdog window lays out its two zones — the buff-config table and
// the live timer bars — relative to each other. Stacked (config above / below the
// bars) or side-by-side (config left / right of them).
public enum BuffWatchdogLayout
{
    ConfigTop,
    ConfigBottom,
    ConfigLeft,
    ConfigRight,
}
