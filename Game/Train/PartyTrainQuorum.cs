using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Train;

// One party member (the leader included) as the quorum sees it.
public readonly record struct PartyTrainParticipant(string Name, bool IsLeader, PartyTrainStatus Status);

public enum PartyTrainVerdict
{
    Idle,   // nobody wants a trip
    Wait,   // somebody does, but the party isn't ready to go yet
    Fire,   // go: train everyone in Trainees
}

// Trainees are the ready members this trip trains. Skipped are the ones the trip
// deliberately leaves behind — a power-leveler outside the level gap, or a straggler
// who won't level inside the max wait. WaitingOn are the stragglers a Wait is still
// holding for. Reason is the one-line log / status text. HasMajority tells the
// coordinator when to start (and stop) the max-wait clock.
public readonly record struct PartyTrainDecision(
    PartyTrainVerdict Verdict,
    IReadOnlyList<string> Trainees,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> WaitingOn,
    string Reason,
    bool HasMajority);

// Decides whether the party goes to train now. Pure: the coordinator feeds it every
// participant's latest report and how long a majority has been ready, and acts on
// the verdict.
//
// The rule is majority-rules, with the leader holding one vote like everyone else —
// a power-leveling leader who won't level for hours must not stall the trip, so the
// leader escorts the party even when it isn't training itself:
//
//   * Blocked members (nothing party-trainable) don't count at all.
//   * A NOT-ready member more than LevelGap levels above the party's (lower) median
//     is a power-leveler: never waited for, and not counted toward the majority.
//   * Everyone left ready → fire.
//   * A strict majority ready → fire as soon as no straggler is worth waiting for.
//     A straggler is worth waiting for while its reported ETA fits inside MaxWait
//     (or is unknown); once the majority has been waiting MaxWait, fire regardless.
//   * Otherwise wait.
public static class PartyTrainQuorum
{
    public static PartyTrainDecision Decide(
        IReadOnlyList<PartyTrainParticipant> participants,
        System.TimeSpan maxWait,
        int levelGap,
        System.TimeSpan? waitedSinceMajority)
    {
        System.ArgumentNullException.ThrowIfNull(participants);

        List<PartyTrainParticipant> live = participants
            .Where(p => p.Status.Readiness != PartyTrainReadiness.Blocked)
            .ToList();
        if (live.Count == 0)
            return Decision(PartyTrainVerdict.Idle, [], [], [], "no one has anything to party-train", false);

        // Power-leveler gap, measured against the LOWER median so that in a two-person
        // party the high character is the outlier (an averaged median would sit halfway
        // and exclude nobody). Only a not-ready member is excluded — a ready one trains.
        List<string> gapSkipped = new();
        if (levelGap > 0 && live.Count >= 2)
        {
            List<int> levels = live.Select(p => p.Status.Level).OrderBy(l => l).ToList();
            int median = levels[(levels.Count - 1) / 2];
            foreach (PartyTrainParticipant p in live)
                if (p.Status.Readiness != PartyTrainReadiness.Ready && p.Status.Level > median + levelGap)
                    gapSkipped.Add(p.Name);
        }

        List<PartyTrainParticipant> eligible = live.Where(p => !gapSkipped.Contains(p.Name)).ToList();
        List<string> ready = eligible
            .Where(p => p.Status.Readiness == PartyTrainReadiness.Ready)
            .Select(p => p.Name)
            .ToList();
        if (ready.Count == 0)
            return Decision(PartyTrainVerdict.Idle, [], gapSkipped, [], "no one is ready to train yet", false);

        List<PartyTrainParticipant> stragglers = eligible
            .Where(p => p.Status.Readiness != PartyTrainReadiness.Ready)
            .ToList();
        if (stragglers.Count == 0)
            return Decision(PartyTrainVerdict.Fire, ready, gapSkipped, [],
                $"all {ready.Count} ready — going to train", true);

        if (ready.Count * 2 <= eligible.Count)
            return Decision(PartyTrainVerdict.Wait, ready, gapSkipped, stragglers.Select(s => s.Name).ToList(),
                $"{ready.Count} of {eligible.Count} ready — waiting for a majority", false);

        bool timedOut = waitedSinceMajority is { } waited && waited >= maxWait;
        List<string> worthWaiting = stragglers
            .Where(s => !timedOut && WorthWaitingFor(s.Status, maxWait))
            .Select(s => s.Name)
            .ToList();
        if (worthWaiting.Count == 0)
        {
            List<string> skipped = gapSkipped.Concat(stragglers.Select(s => s.Name)).ToList();
            return Decision(PartyTrainVerdict.Fire, ready, skipped, [],
                timedOut
                    ? $"{ready.Count} of {eligible.Count} ready and the max wait is up — going without {string.Join(", ", stragglers.Select(s => s.Name))}"
                    : $"{ready.Count} of {eligible.Count} ready — {string.Join(", ", stragglers.Select(s => s.Name))} won't level inside the max wait",
                true);
        }

        return Decision(PartyTrainVerdict.Wait, ready, gapSkipped, worthWaiting,
            $"{ready.Count} of {eligible.Count} ready — holding for {string.Join(", ", worthWaiting)}", true);
    }

    // A straggler is worth holding for while it's due inside the max wait. An unknown
    // ETA (no earn rate yet) gets the benefit of the doubt — the max-wait timer is what
    // eventually stops us waiting on it.
    private static bool WorthWaitingFor(PartyTrainStatus s, System.TimeSpan maxWait) =>
        s.EtaSeconds < 0 || s.EtaSeconds <= maxWait.TotalSeconds;

    private static PartyTrainDecision Decision(
        PartyTrainVerdict verdict, List<string> trainees, List<string> skipped,
        List<string> waitingOn, string reason, bool hasMajority) =>
        new(verdict, trainees, skipped, waitingOn, reason, hasMajority);
}
