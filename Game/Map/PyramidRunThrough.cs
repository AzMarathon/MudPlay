using MudPlay.Models.Profile;

namespace MudPlay.Game.Map;

// Floors 1 and 2 of the Great Pyramid are run, not worked: floor 1 is on a timer
// and standing in floor 2 hurts more the longer it lasts. So Auto Combat, Nuke,
// Rest, Get Items, Get Cash, Search, Hide and Light are switched off for the two
// floors (the toolbar shows them off) and back on at floor 3; Heal, Bless and Sneak
// stay as the player has them (user, 2026-10-03). One the player switches back on
// is theirs: it runs, and where it holds movement the climb waits for it.
public static class PyramidRunThrough
{
    // Whether the engine behind a movement gate is switched on. Only four of the
    // switched-off engines hold movement at all. A gate with no engine among them
    // (party waits, conditions, sneaking, gear swaps) is never "on" here: the two
    // floors walk through those regardless.
    public static bool GateEngineOn(string gate, AutoActionDefaults live) => gate switch
    {
        MovementCoordinator.CombatGate
            or MovementCoordinator.AbandonedCombatGate
            or MovementCoordinator.CombatRedisplaySettleGate
            or MovementCoordinator.SummonDeathSettleGate
            or MovementCoordinator.DarkRoomSettleGate => live.AutoCombat,
        MovementCoordinator.HealthRecoveryGate
            or MovementCoordinator.ManaRecoveryGate => live.AutoRest,
        MovementCoordinator.AcquisitionGate => live.AutoGetItems || live.AutoGetCash,
        MovementCoordinator.SearchGate => live.AutoSearch,
        _ => false,
    };
}
