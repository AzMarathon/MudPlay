namespace MudPlay.Game.Spells;

// When a sneaking character holds its maintenance casts (buffs, cures, top-off
// heals). Casting breaks sneak, and there are two ways that goes wrong:
//   - our move is in flight: the server runs commands in order, so the cast lands
//     in the room we're ENTERING, unseen, and we arrive unsneaked;
//   - an NPC is here and we're slipping past it (combat won't clear the room), or
//     a backstab opener is still owed and must be the first thing out of stealth.
// Anywhere else the cast goes out and the character re-sneaks after it.
public static class StealthCastHold
{
    public static bool ShouldHold(
        bool autoSneak, bool moveInFlight, bool npcInRoom, bool combatWillClearRoom, bool backstabOpenerPending)
    {
        if (!autoSneak) return false;
        if (moveInFlight) return true;
        return npcInRoom && (!combatWillClearRoom || backstabOpenerPending);
    }
}
