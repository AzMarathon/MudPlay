namespace MudPlay.Game.Combat;

// One cause's share of Time Analysis's Waiting (TimeAnalysisStats.WaitingBy).
public readonly record struct WaitCauseTime(string Cause, TimeSpan Time);
