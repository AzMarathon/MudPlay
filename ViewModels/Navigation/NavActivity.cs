using MudPlay.Game.Map;

namespace MudPlay.ViewModels.Navigation;

// What a movement engine is doing right now, for the Navigation top bar.
public enum NavActivityKind { None, Moving, Fighting, Waiting, Paused }

// How a top-bar chip after the status line reads: a hold that stops movement, a
// brief settle beat while moving, or an errand trip the walker is on.
public enum NavChipTone { Wait, Beat, Trip }

// Pure mapping from the live MovementCoordinator gate state to a plain-English
// "what is the engine doing / why is it held" phrase. Split out of
// NavigationViewModel so it's unit-testable and so a NEW gate can't silently slip
// through to a raw internal name on the UI — NavActivityGateLabelsTest asserts
// every MovementCoordinator *Gate constant resolves to a real label here.
//
// The order is a priority scan: the most important reason a human wants to see
// wins (an explicit user pause, then combat, then severe self-states, then
// recovery / party holds, then the brief engine-wait beats). It mirrors the gate
// tiers on MovementCoordinator; keep new gates in the tier that matches their
// urgency rather than appending blindly.
public static class NavActivity
{
    // Every hold the engine can be under, in priority order, with its plain label.
    // Moving-kind entries are the brief per-room settle beats: a moment in the
    // middle of moving, not a real stop, so they sit last and any real wait wins.
    private static readonly (string Gate, string Label, NavActivityKind Kind)[] Holds =
    [
        (MovementCoordinator.AbandonedCombatGate, "leaving a fight", NavActivityKind.Waiting),
        (MovementCoordinator.MortallyWoundedGate, "mortally wounded", NavActivityKind.Waiting),
        // Our own confusion holds navigation locally (the leader/solo analogue of a
        // confused follower's @wait) — our own affliction, same tier as held.
        (MovementCoordinator.ConfusionGate, "confused", NavActivityKind.Waiting),
        (MovementCoordinator.HeldGate, "held", NavActivityKind.Waiting),
        (MovementCoordinator.FearGate, "afraid", NavActivityKind.Waiting),
        // Recovery holds — resting / meditating below a rest floor.
        (MovementCoordinator.HealthRecoveryGate, "resting (low HP)", NavActivityKind.Waiting),
        (MovementCoordinator.ManaRecoveryGate, "meditating (low mana)", NavActivityKind.Waiting),
        (MovementCoordinator.CorpseRecoveryGate, "recovering corpse", NavActivityKind.Waiting),
        // Party holds — waiting on other members.
        (MovementCoordinator.PartyWaitGate, "party asked to wait", NavActivityKind.Waiting),
        (MovementCoordinator.AllyDownGate, "ally is down", NavActivityKind.Waiting),
        (MovementCoordinator.PartyVitalsGate, "party member hurt", NavActivityKind.Waiting),
        (MovementCoordinator.MemberDisconnectGate, "member reconnecting", NavActivityKind.Waiting),
        (MovementCoordinator.PartyInviteGate, "for invitee to join", NavActivityKind.Waiting),
        (MovementCoordinator.FollowerGate, "following leader", NavActivityKind.Waiting),
        // Auto-engines kill switch off — a queued walk / loop / lair is planned but
        // held here until Auto-All is restored; the single most common "why isn't it
        // moving?" for a queued route.
        (MovementCoordinator.AutoAllGate, "auto-engines off (Auto-All)", NavActivityKind.Waiting),
        // In-room engine actions after a fight clears.
        (MovementCoordinator.SearchGate, "searching the room", NavActivityKind.Waiting),
        (MovementCoordinator.AcquisitionGate, "looting", NavActivityKind.Waiting),
        (MovementCoordinator.GhSortGate, "sorting items (Roomba)", NavActivityKind.Waiting),
        (MovementCoordinator.GearSwapGate, "changing gear", NavActivityKind.Waiting),
        (MovementCoordinator.SneakCooldownGate, "sneak on cooldown", NavActivityKind.Waiting),
        (MovementCoordinator.SneakSettleGate, "sneaking", NavActivityKind.Waiting),
        (MovementCoordinator.SneakCastGate, "casting before re-sneaking", NavActivityKind.Waiting),
        (MovementCoordinator.SellingGate, "selling", NavActivityKind.Waiting),
        (MovementCoordinator.SellDetourGate, "stopping to go sell", NavActivityKind.Waiting),
        (MovementCoordinator.DarkRoomSettleGate, "checking the dark", NavActivityKind.Moving),
        (MovementCoordinator.CombatRedisplaySettleGate, "checking for an ambush", NavActivityKind.Moving),
        (MovementCoordinator.SummonDeathSettleGate, "checking for a summon", NavActivityKind.Moving),
    ];

    public static (string Text, NavActivityKind Kind) Describe(
        IReadOnlyCollection<string> gates, bool isPaused, bool isMovementPrevented)
    {
        ArgumentNullException.ThrowIfNull(gates);

        // User pause and combat outrank everything: an explicit pause is the user's
        // own doing, and mid-fight "Fighting" is the more useful readout than a hold.
        if (gates.Contains(MovementCoordinator.UserGate)) return ("Paused", NavActivityKind.Paused);
        if (gates.Contains(MovementCoordinator.CombatGate)) return ("Fighting", NavActivityKind.Fighting);

        // Our own held/mortally-wounded state stops movement server-side. The
        // condition flag is authoritative (SelfHeldResponder asserts HeldGate off the
        // same edge), so it's checked ahead of the gate scan.
        if (isMovementPrevented) return ("Waiting — held", NavActivityKind.Waiting);

        // Nothing is gating and we're not held → genuinely moving.
        if (!isPaused) return ("Moving", NavActivityKind.Moving);

        foreach ((string gate, string label, NavActivityKind kind) in Holds)
            if (gates.Contains(gate)) return ($"{kind} — {label}", kind);

        string first = gates.FirstOrDefault() ?? "?";
        return ($"Waiting — {first}", NavActivityKind.Waiting);
    }

    // Every hold in force right now, in priority order, for the top bar's hold
    // chips. User pause and combat are left out: the live state chip already reads
    // Paused / Fighting. The held condition flag stands in for HeldGate, since it
    // flips first and the two describe the same thing.
    public static IReadOnlyList<(string Label, NavChipTone Tone)> ActiveHolds(
        IReadOnlyCollection<string> gates, bool isMovementPrevented)
    {
        ArgumentNullException.ThrowIfNull(gates);
        List<(string, NavChipTone)> holds = [];
        if (isMovementPrevented) holds.Add(("held", NavChipTone.Wait));
        foreach ((string gate, string label, NavActivityKind kind) in Holds)
            if (gates.Contains(gate) && !(isMovementPrevented && gate == MovementCoordinator.HeldGate))
                holds.Add((label, kind == NavActivityKind.Moving ? NavChipTone.Beat : NavChipTone.Wait));
        foreach (string gate in gates)
            if (gate is not MovementCoordinator.UserGate and not MovementCoordinator.CombatGate
                && !Array.Exists(Holds, h => h.Gate == gate))
                holds.Add((gate, NavChipTone.Wait));
        return holds;
    }

    // The specific wait reason to fold into the top-bar status line (e.g. "Walking
    // to X … — resting (low HP)"). Only a Waiting carries detail the state chip
    // doesn't already show — the chip's short word already says "Fighting" / "Paused"
    // / "Moving", so folding those onto the line would just repeat the chip beside
    // it. Null for everything but Waiting, so the line stays clean.
    public static string? HoldSuffix(string text, NavActivityKind kind)
    {
        const string waitingPrefix = "Waiting — ";
        if (kind != NavActivityKind.Waiting) return null;
        return text.StartsWith(waitingPrefix, StringComparison.Ordinal)
            ? text[waitingPrefix.Length..]
            : text;
    }
}
