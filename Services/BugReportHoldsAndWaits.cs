using System.Text;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// The bug report's "Holds and waits" section: everything that can make the client
// just sit there, in one place near the top. The send gate and what it dropped,
// every movement gate with who raised it, why and since when, how long each
// engine has been in its state, and each answer an engine is still waiting for.
public static class BugReportHoldsAndWaits
{
    public const string Heading = "Holds and waits";

    // How many of the movement gates' last transitions are printed.
    private const int GateTransitionsShown = 40;

    // The states EngineStateWatch follows. Called once, when every engine exists.
    public static void RegisterProbes(AppServices svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        EngineStateWatch watch = svc.StateWatch;
        watch.Add("In the game", () => svc.InGameCapture.InGame ? "yes" : svc.InGameCapture.AtBoardMenu ? "no (at the board's menu)" : "no");
        watch.Add("Master switch (Auto-All)", () => svc.AutoModeController.KillSwitchEngaged ? "OFF" : "on");
        watch.Add("Send gate", () => svc.EngineGate.IsLocked ? "held: " + string.Join(", ", svc.EngineGate.Holds) : "open");
        watch.Add("Movement", () => svc.MovementControl.State.ToString());
        watch.Add("Movement gates", () => string.Join(", ", svc.MovementCoordinator.AssertedGates.OrderBy(g => g, StringComparer.Ordinal)) is { Length: > 0 } g ? g : "(none)", keep: 12);
        watch.Add("Walker", () => svc.Walker.State.ToString());
        watch.Add("Loop runner", () => svc.LoopRunner.State.ToString());
        watch.Add("Auto-Lair", () => svc.AutoLair.Phase.ToString());
        watch.Add("Roomba", () => svc.GhSweep.Phase.ToString());
        watch.Add("Recovery tier", () => svc.Recovery.CurrentTier.ToString());
        watch.Add("Door handler", () => svc.Door.CurrentState.ToString());
        watch.Add("Winch handler", () => svc.Winch.CurrentState.ToString());
        watch.Add("Trap disarm", () => svc.TrapDisarm.CurrentState.ToString());
        watch.Add("Hidden-exit search", () => svc.HiddenSearch.IsBusy ? "searching" : "idle");
        watch.Add("Stealth", () => svc.Stealth.State.ToString());
        watch.Add("In combat (prompt)", () => svc.PlayerState.InCombat ? "yes" : "no");
        watch.Add("Position (prompt)", () => svc.PlayerState.Position.ToString());
        watch.Add("Room", () => svc.RoomTracker.State.CurrentRoom is { } room
            ? $"{room.Key.Map}/{room.Key.Room} ({svc.RoomTracker.State.Confidence})" : "(unknown)", keep: 20);
    }

    public static string Build(AppServices svc, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(svc);
        StringBuilder sb = new();

        sb.Append("**Send gate** (every engine's commands pass it; held, they are dropped, not queued)\n\n");
        Part(sb, () => SendGate(sb, svc.EngineGate));

        sb.Append("\n**Movement gates** (any one asserted pauses every walk and loop)\n\n");
        Part(sb, () => MovementGates(sb, svc.MovementCoordinator, now));

        sb.Append("\n**Time in state** (read once a second, so good to the second; `≥` means unchanged since the client started watching)\n\n");
        Part(sb, () => States(sb, svc.StateWatch.Snapshot(now), now));

        sb.Append("\n**Waiting on**\n\n");
        Waits(sb, "Walker", () => svc.Walker.DescribeWaits());
        Waits(sb, "Loop runner", () => svc.LoopRunner.DescribeWaits());
        Waits(sb, "Health (rest / flee)", () => svc.Health.DescribeWaits());
        Waits(sb, "Stealth", () => svc.Stealth.DescribeWaits());

        sb.Append("\n**Casting** (why a cast did or didn't go out)\n\n");
        Part(sb, () =>
        {
            Kv(sb, "Cast clocks", svc.Cast.DescribeState());
            Kv(sb, "Between-round slot used this round", svc.CastDirector.BetweenRoundSlotUsed ? "yes" : "no");
            Kv(sb, "Between-round slot may still be spent (a lair wait reads this)",
                svc.CastDirector.BetweenRoundSlotMayBeSpent ? "yes" : "no");
            Kv(sb, "A cast is due", svc.CastDirector.HasCastDue() ? "yes" : "no");
            Kv(sb, "A cast held for a kept sneak", svc.CastDirector.HasSneakHeldCast ? "yes" : "no");
            Kv(sb, "Rest-time heal held for standing (out of combat, not resting, not following)",
                svc.CastDirector.RestHealHeldStanding ? "yes" : "no");
        });
        return sb.ToString();
    }

    internal static void SendGate(StringBuilder sb, EngineSendGate gate)
    {
        Kv(sb, "State", gate.IsLocked ? "HELD by " + string.Join(", ", gate.Holds) : "open");
        Kv(sb, "Last client command sent", gate.LastClientCommandText is { } last ? $"`{last}`" : "(none yet)");
        Kv(sb, "Commands dropped by a hold this session", gate.DroppedCount == 0
            ? "0"
            : $"{gate.DroppedCount}; the last at {gate.LastDropped!.Value.At:HH:mm:ss.fff}: `{gate.LastDropped.Value.Command}`"
              + $" from {gate.LastDropped.Value.Sender ?? "(unnamed)"}");
    }

    internal static void MovementGates(StringBuilder sb, MovementCoordinator coordinator, DateTimeOffset now)
    {
        IReadOnlyList<GateTransitionEntry> history = coordinator.History;
        IReadOnlyCollection<string> asserted = coordinator.AssertedGates;
        if (asserted.Count == 0) sb.Append("- _(none asserted)_\n");
        foreach (string gate in asserted.OrderBy(g => g, StringComparer.Ordinal))
        {
            // The transition that raised it, if the history still reaches back
            // that far.
            GateTransitionEntry? raised = null;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(history[i].Gate, gate, StringComparison.OrdinalIgnoreCase)) continue;
                if (history[i].Asserted) raised = history[i];
                break;
            }
            sb.Append("- **").Append(gate).Append("**: ");
            if (raised is { } r)
                sb.Append("since ").Append(r.Timestamp.ToString("HH:mm:ss")).Append(" (").Append(Age(now - r.Timestamp)).Append(')')
                  .Append(", by ").Append(string.IsNullOrWhiteSpace(r.Asserter) ? "(unnamed)" : r.Asserter)
                  .Append(string.IsNullOrWhiteSpace(r.Reason) ? string.Empty : ": " + r.Reason);
            else
                sb.Append("raised before the ").Append(history.Count).Append(" transitions still remembered");
            sb.Append('\n');
        }
        IReadOnlyCollection<string> parked = coordinator.ParkedGates;
        if (parked.Count > 0)
            Kv(sb, "Owed but not asserted (master switch off)", string.Join(", ", parked.OrderBy(g => g, StringComparer.Ordinal)));

        int shown = Math.Min(GateTransitionsShown, history.Count);
        sb.Append("\nLast ").Append(shown).Append(" gate transitions (oldest first):\n\n");
        if (shown == 0) { sb.Append("_(none this session)_\n"); return; }
        sb.Append("```\n");
        for (int i = history.Count - shown; i < history.Count; i++)
        {
            GateTransitionEntry t = history[i];
            sb.Append(t.Timestamp.ToString("HH:mm:ss.fff")).Append("  ").Append(t.Asserted ? "asserted" : "cleared ")
              .Append("  ").Append(t.Gate);
            if (!string.IsNullOrWhiteSpace(t.Asserter)) sb.Append("  by ").Append(t.Asserter);
            if (!string.IsNullOrWhiteSpace(t.Reason)) sb.Append("  — ").Append(t.Reason);
            sb.Append('\n');
        }
        sb.Append("```\n");
    }

    internal static void States(StringBuilder sb, IReadOnlyList<EngineStateWatch.Reading> readings, DateTimeOffset now)
    {
        if (readings.Count == 0) { sb.Append("_(no states watched)_\n"); return; }
        foreach (EngineStateWatch.Reading r in readings)
        {
            sb.Append("- **").Append(r.Name).Append("**: ").Append(r.Value)
              .Append(" for ").Append(r.SinceWatchStart ? "≥ " : string.Empty).Append(Age(now - r.Since))
              .Append(" (since ").Append(r.Since.ToString("HH:mm:ss")).Append(')');
            if (r.Recent.Count > 0)
            {
                sb.Append("; before that: ");
                // Newest first here: the reader walks back from the state above.
                for (int i = r.Recent.Count - 1; i >= 0; i--)
                {
                    sb.Append(r.Recent[i].From).Append(" until ").Append(r.Recent[i].At.ToString("HH:mm:ss"));
                    if (i > 0) sb.Append(" ← ");
                }
            }
            sb.Append('\n');
        }
    }

    private static void Waits(StringBuilder sb, string engine, Func<IReadOnlyList<string>> read) =>
        Part(sb, () =>
        {
            IReadOnlyList<string> waits = read();
            Kv(sb, engine, waits.Count == 0 ? "(nothing)" : string.Join("; ", waits));
        });

    // One engine that can't be read must not cost the reader the others.
    private static void Part(StringBuilder sb, Action write)
    {
        try { write(); }
        catch (Exception ex) { sb.Append("- _(could not read: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append(")_\n"); }
    }

    internal static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalSeconds < 120 ? $"{age.TotalSeconds:0} s"
            : age.TotalMinutes < 120 ? $"{age.TotalMinutes:0} min"
            : $"{age.TotalHours:0.#} h";
    }

    private static void Kv(StringBuilder sb, string key, string value) =>
        sb.Append("- **").Append(key).Append("**: ").Append(value).Append('\n');
}
