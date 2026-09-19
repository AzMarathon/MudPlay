using System;
using System.IO;

namespace MudPlay.Services;

// One player's report of who last saw the hydra trainer dead. Never
// self-detected — the death isn't reliably observable first-hand by everyone
// who needs to know, so this only ever changes when a permitted player
// explicitly reports it over chat via @hydra dead.
public sealed record HydraDeathReport(string ReportedBy, DateTimeOffset At);

// Persisted per-set (realm-wide, shared across the user's characters — same
// scope as BossTimerStore) record of the hydra trainer's last reported death:
// who reported it and when. Written to {set}/hydra-report.json so a restart
// doesn't lose it. Deliberately a single record, not a history — @hydra
// status only ever answers "the most recent report", and a Report() call
// overwrites whatever was there before.
public sealed class HydraReportStore
{
    private readonly LogService? _log;
    private HydraDeathReport? _current;

    public string? ActiveSet { get; private set; }

    // Fires after a change to the tracked report (a new @hydra dead, or a
    // set switch loading a different realm's record) so a view can refresh
    // without polling.
    public event Action? Changed;

    public HydraReportStore(LogService? log = null) => _log = log;

    public void OnActiveSetChanged(string? setName)
    {
        ActiveSet = string.IsNullOrWhiteSpace(setName) ? null : setName;
        _current = ActiveSet is not null
            ? JsonStore.Load<HydraDeathReport>(AppPaths.HydraReportFile(ActiveSet))
            : null;
        Changed?.Invoke();
    }

    public HydraDeathReport? Current => _current;

    // Record a kill report at now (UTC) and persist. reportedBy is the
    // reporting player's in-game name, as classified off the chat line —
    // not validated further (a report is trusted the same way any other
    // permitted @-command is).
    public void Report(string reportedBy, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(reportedBy)) return;
        _current = new HydraDeathReport(reportedBy, at.ToUniversalTime());
        Persist();
        _log?.Info("Hydra", $"kill reported by {reportedBy} at {_current.At:u}");
        Changed?.Invoke();
    }

    // Clear the tracked report (a mistaken/false report).
    public void Reset()
    {
        if (_current is null) return;
        _current = null;
        Persist();
        _log?.Info("Hydra", "report cleared");
        Changed?.Invoke();
    }

    private void Persist()
    {
        if (ActiveSet is null) return;

        // Same reasoning as BossTimerStore.Persist: this is convenience
        // bookkeeping fired from a chat-line handler — a write failure must
        // never crash the client mid-session. Log it and carry on; the
        // in-memory report still stands and the next report re-attempts.
        string path = AppPaths.HydraReportFile(ActiveSet);
        try
        {
            if (_current is { } report) JsonStore.Save(path, report);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _log?.Warn("Hydra", $"failed to persist hydra report: {ex.Message}");
        }
    }
}
