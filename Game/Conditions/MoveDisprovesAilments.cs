using MudPlay.Game.Map;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Conditions;

// A move that lands, shown by its room's own display, is outside proof against two
// latched ailments whose wear-off line can be missed: a held or knocked-down
// character can't walk and isn't dragged by a leader, and a blinded one is shown only
// "You are blind." in place of the room (GAME_MECHANICS "Moving while blind"). Left
// latched, each stands until Reset States: the walker sat "Paused by: Held" and the
// hold cure re-cast every window (report paradigm-20261003-161904); a cleric cast
// cure blindness on itself every 15 s while walking a loop it could plainly see
// (report CureBlindlessBugReport, 2026-10-22). Like ConditionTracker.ClearFlag's
// other uses this is a safe over-clear: a blindness that is still on shows again on
// the next move.
public static class MoveDisprovesAilments
{
    public static void Wire(RoomTracker tracker, ConditionTracker conditions)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(conditions);
        tracker.MoveConfirmed += () =>
        {
            conditions.ClearFlag(MessageFlags.MovementPrevented, "a move went through");
            conditions.ClearFlag(MessageFlags.Blinded, "a move's room display was seen", onlyWholly: true);
        };
    }
}
