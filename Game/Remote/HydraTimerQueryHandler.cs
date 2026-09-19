using System;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Handler for @hydra — a manually-reported kill timer for a trainer whose
// death isn't reliably observable first-hand by everyone who needs to know
// (unlike @timer's bosses, which self-detect from combat telemetry). Gated
// by the QueryHydraTimer permission.
//   @hydra dead     — records a kill: reporter = the sender, time = now.
//   @hydra status   — reports the last recorded kill: who, how long ago.
// Replies route back on whichever channel the command arrived on (telepath,
// gangpath, local, or broadcast) — same as every other @-command.
public sealed class HydraTimerQueryHandler : IDisposable
{
    private const string UsageReply = "usage: @hydra dead | @hydra status";

    private readonly RemoteCommandManager _engine;
    private readonly HydraReportStore _reports;
    private readonly LogService? _log;
    private bool _disposed;

    public HydraTimerQueryHandler(RemoteCommandManager engine, HydraReportStore reports, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(reports);
        _engine = engine;
        _reports = reports;
        _log = log;

        if (!RemoteCommandCatalog.TryGetCategory("@hydra", out PlayerRemoteControls category))
            throw new InvalidOperationException(
                "RemoteCommandCatalog missing entry for '@hydra'. Add it to the Map before registering.");
        _engine.RegisterHandler("@hydra", category, OnHydra);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@hydra");
    }

    private void OnHydra(RemoteCommandContext ctx)
    {
        if (ctx.Args.Count == 0)
        {
            ctx.Reply(UsageReply);
            return;
        }

        if (ctx.Args[0].Equals("dead", StringComparison.OrdinalIgnoreCase))
        {
            _reports.Report(ctx.Sender, DateTimeOffset.UtcNow);
            _log?.Info("Hydra", $"kill reported by {ctx.Sender} on {ctx.Channel}");
            ctx.Reply($"hydra kill recorded, reported by {ctx.Sender}");
            return;
        }

        if (ctx.Args[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            if (_reports.Current is not { } report)
            {
                ctx.Reply("no hydra kill reported yet");
                return;
            }
            string elapsed = BossTimerMath.FormatHours((DateTimeOffset.UtcNow - report.At).TotalHours);
            ctx.Reply($"hydra last reported dead by {report.ReportedBy}, {elapsed} ago");
            return;
        }

        ctx.Reply(UsageReply);
    }
}
