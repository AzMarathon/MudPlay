namespace MudPlay.Models.Settings;

// The Program Log window's switches, kept in GlobalSettings.LogDiagnostics.
//
// Global tier (user, 2026-10-02): they describe what the client records about
// itself, not a character, and Auto-collect logs has to hold from launch and
// across character switches to capture a whole session. Debug + Combat tracing
// default ON so a Program Log already carries the decision-trail a bug report
// needs (the common case was a capture with both off and nothing to diagnose).
// On-disk log generation and hop-timing stay off — those are heavier, opt-in
// troubleshooting affordances. The live mirror is Services.LogDiagnosticState;
// AppServices applies this at launch and writes it back when a Log-pane toggle
// flips.
public sealed class LogDiagnosticsSettings
{
    // Gate for the generation-gated Debug channel. Default on.
    public bool Debug { get; set; } = true;

    // Gate for the generation-gated Combat channel. Default on.
    public bool Combat { get; set; } = true;

    // Gate for the on-disk diagnostic files (program / memory / combat-trace /
    // performance writers under Logs). Default off. When on, the client
    // generates all of them for the session.
    public bool AutoCollect { get; set; }

    // Gate for the navigation hop-timing calibration trace. Default off. When
    // on, HopTimingCalibrator emits one Info line per confirmed room hop.
    public bool HopTiming { get; set; }

    // Gate for Game.MessageCandidateWatcher. Default on — the point of the
    // feature is catching silent Messages-catalogue misses, matching Debug/
    // Combat's "default to visibility" rationale.
    public bool CaptureUnrecognizedMessages { get; set; } = true;

    // Gate for Services.SessionStatsLog, and how often it writes. Default off,
    // every 5 minutes.
    public bool SessionStatistics { get; set; }
    public int SessionStatisticsMinutes { get; set; } = 5;
}
