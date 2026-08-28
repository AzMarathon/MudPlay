using MudPlay.Game.Map;

namespace MudPlay.ViewModels.Navigation;

// What a movement engine is doing right now, for the Navigation top bar.
public enum NavActivityKind { None, Moving, Fighting, Waiting, Paused }

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
    public static (string Text, NavActivityKind Kind) Describe(
        IReadOnlyCollection<string> gates, bool isPaused, bool isMovementPrevented)
    {
        ArgumentNullException.ThrowIfNull(gates);

        // User pause and combat outrank everything: an explicit pause is the user's
        // own doing, and mid-fight "Fighting" is the more useful readout than a hold.
        if (gates.Contains(MovementCoordinator.UserGate)) return ("Paused", NavActivityKind.Paused);
        if (gates.Contains(MovementCoordinator.CombatGate)) return ("Fighting", NavActivityKind.Fighting);
        if (gates.Contains(MovementCoordinator.AbandonedCombatGate))
            return ("Waiting — leaving a fight", NavActivityKind.Waiting);

        // Our own held/mortally-wounded state stops movement server-side. The
        // condition flag is authoritative (SelfHeldResponder asserts HeldGate off the
        // same edge), so it's checked ahead of the gate scan.
        if (isMovementPrevented) return ("Waiting — held", NavActivityKind.Waiting);
        if (gates.Contains(MovementCoordinator.MortallyWoundedGate))
            return ("Waiting — mortally wounded", NavActivityKind.Waiting);

        // Nothing is gating and we're not held → genuinely moving.
        if (!isPaused) return ("Moving", NavActivityKind.Moving);

        // Our own confusion holds navigation locally (the leader/solo analogue of a
        // confused follower's @wait) — our own affliction, same tier as held above.
        if (gates.Contains(MovementCoordinator.ConfusionGate))
            return ("Waiting — confused", NavActivityKind.Waiting);
        if (gates.Contains(MovementCoordinator.HeldGate))
            return ("Waiting — held", NavActivityKind.Waiting);

        // Recovery holds — resting / meditating below a rest floor.
        if (gates.Contains(MovementCoordinator.HealthRecoveryGate))
            return ("Waiting — resting (low HP)", NavActivityKind.Waiting);
        if (gates.Contains(MovementCoordinator.ManaRecoveryGate))
            return ("Waiting — meditating (low mana)", NavActivityKind.Waiting);
        if (gates.Contains(MovementCoordinator.CorpseRecoveryGate))
            return ("Waiting — recovering corpse", NavActivityKind.Waiting);

        // Party holds — waiting on other members.
        if (gates.Contains(MovementCoordinator.PartyWaitGate))
            return ("Waiting — party asked to wait", NavActivityKind.Waiting);
        if (gates.Contains(MovementCoordinator.AllyDownGate))
            return ("Waiting — ally is down", NavActivityKind.Waiting);
        if (gates.Contains(MovementCoordinator.PartyVitalsGate))
            return ("Waiting — party member hurt", NavActivityKind.Waiting);
        if (gates.Contains(MovementCoordinator.MemberDisconnectGate))
            return ("Waiting — member reconnecting", NavActivityKind.Waiting);
        if (gates.Contains(MovementCoordinator.PartyInviteGate))
            return ("Waiting — for invitee to join", NavActivityKind.Waiting);
        if (gates.Contains(MovementCoordinator.FollowerGate))
            return ("Waiting — following leader", NavActivityKind.Waiting);

        // Auto-engines kill switch off — a queued walk / loop / lair is planned but
        // held here until Auto-All is restored; the single most common "why isn't it
        // moving?" for a queued route.
        if (gates.Contains(MovementCoordinator.AutoAllGate))
            return ("Waiting — auto-engines off (Auto-All)", NavActivityKind.Waiting);

        // In-room engine actions after a fight clears.
        if (gates.Contains(MovementCoordinator.SearchGate))
            return ("Waiting — searching the room", NavActivityKind.Waiting);
        if (gates.Contains(MovementCoordinator.AcquisitionGate))
            return ("Waiting — looting", NavActivityKind.Waiting);
        if (gates.Contains(MovementCoordinator.GhSortGate))
            return ("Waiting — sorting items (Roomba)", NavActivityKind.Waiting);
        if (gates.Contains(MovementCoordinator.GearSwapGate))
            return ("Waiting — changing gear", NavActivityKind.Waiting);

        // Brief per-room settle beats while a room reveals a late-arriving hostile.
        // They're a moment in the middle of moving, not a real stop, so they read as
        // Moving and sit last — any real wait above wins.
        if (gates.Contains(MovementCoordinator.DarkRoomSettleGate))
            return ("Moving — checking the dark", NavActivityKind.Moving);
        if (gates.Contains(MovementCoordinator.CombatRedisplaySettleGate))
            return ("Moving — checking for an ambush", NavActivityKind.Moving);
        if (gates.Contains(MovementCoordinator.SummonDeathSettleGate))
            return ("Moving — checking for a summon", NavActivityKind.Moving);

        string first = gates.FirstOrDefault() ?? "?";
        return ($"Waiting — {first}", NavActivityKind.Waiting);
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
