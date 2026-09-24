using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Train;

// One party member (the leader included) as the quorum sees it.
public readonly record struct PartyTrainParticipant(string Name, bool IsLeader, PartyTrainStatus Status);

public enum PartyTrainVerdict
{
    Idle,   // nobody wants a trip
    Wait,   // somebody does, but not enough of the party is ready yet
    Fire,   // go: train everyone in Trainees
}

// Trainees are the ready members this trip trains. Skipped are the ones the trip
// deliberately leaves behind — a power-leveler outside the level gap, or a member
// not ready yet once enough others are. WaitingOn are the not-ready members a Wait
// is still short of. Reason is the one-line log / status text.
public readonly record struct PartyTrainDecision(
    PartyTrainVerdict Verdict,
    IReadOnlyList<string> Trainees,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> WaitingOn,
    string Reason);

// Decides whether the party goes to train now. Pure: the coordinator feeds it every
// participant's latest report and acts on the verdict.
//
// The trip goes once at least minReady members are ready, the leader counting as one
// member like everyone else — a power-leveling leader who won't level for hours must
// not stall the trip, so the leader escorts the party even when it isn't training:
//
//   * Blocked members (nothing party-trainable) and Off ones don't count at all.
//   * A NOT-ready member more than LevelGap levels above the party's (lower) median
//     is a power-leveler: excluded, so it can't hold the "everyone ready" case.
//   * Everyone left ready → fire, even when that's fewer than minReady (a party
//     smaller than the setting would otherwise never go).
//   * At least minReady ready → fire; whoever isn't ready yet sits this trip out.
//   * Otherwise wait.
public static class PartyTrainQuorum
{
    public static PartyTrainDecision Decide(
        IReadOnlyList<PartyTrainParticipant> participants,
        int levelGap,
        int minReady)
    {
        System.ArgumentNullException.ThrowIfNull(participants);

        List<PartyTrainParticipant> live = participants
            .Where(p => p.Status.Readiness is not (PartyTrainReadiness.Blocked or PartyTrainReadiness.Off))
            .ToList();
        if (live.Count == 0)
            return Decision(PartyTrainVerdict.Idle, [], [], [], "no one has anything to party-train");

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
            return Decision(PartyTrainVerdict.Idle, [], gapSkipped, [], "no one is ready to train yet");

        List<string> notReady = eligible
            .Where(p => p.Status.Readiness != PartyTrainReadiness.Ready)
            .Select(p => p.Name)
            .ToList();
        if (notReady.Count == 0)
            return Decision(PartyTrainVerdict.Fire, ready, gapSkipped, [],
                $"all {ready.Count} ready — going to train");

        int needed = System.Math.Max(1, minReady);
        if (ready.Count >= needed)
            return Decision(PartyTrainVerdict.Fire, ready, gapSkipped.Concat(notReady).ToList(), [],
                $"{ready.Count} ready (need {needed}) — going without {string.Join(", ", notReady)}");

        return Decision(PartyTrainVerdict.Wait, ready, gapSkipped, notReady,
            $"{ready.Count} of {needed} needed ready — waiting on {string.Join(", ", notReady)}");
    }

    private static PartyTrainDecision Decision(
        PartyTrainVerdict verdict, List<string> trainees, List<string> skipped,
        List<string> waitingOn, string reason) =>
        new(verdict, trainees, skipped, waitingOn, reason);
}
