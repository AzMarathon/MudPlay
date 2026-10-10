using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Recovery;

// Tells whether a hang-up was a death. On a board that penalises the hang-up, the
// routine that takes the character out of the game ends in the death check
// (GAME_MECHANICS "Hang-up / lost carrier"): a character at or below 0 HP is
// killed outright, and a standing one when the share of max HP the penalty takes
// puts it under the death threshold. That happens after the link is gone, so no
// death line is ever seen, and the next entry finds the character standing in the
// temple at full HP with a life less and nothing on it.
//
// What a death leaves that the client can read on the way back in:
//   - HP higher than it was, and above 0. A death sets it to the maximum; nothing
//     else raises it while the character is out of the game, and a penalty that
//     didn't kill only lowered it.
//   - one life fewer. Lives go down by nothing else.
//   - another room, with something the character held gone. Weaker: the map can
//     be wrong about either room, a board can move a character that hangs up for
//     reasons of its own, a penalty that drops items takes some without a death,
//     and the list of what was held is as old as the last profile save if the
//     client itself went down.
// A death record sends Death Recovery walking for a pile, so a wrong one costs
// more than a missing one. Hence: HP risen always, and then the lives when both
// counts are known; only when one of them isn't do the room and the pack stand in.
public static class HangupDeath
{
    // Whether that hang-up can have killed: the realm's settings penalise it, and
    // the character was dropped or low enough for the largest share to drop it.
    // The death threshold itself is the board's and not known for sure, so "to 0
    // or below" is as far as this goes; what is found on the way back in decides.
    //   hpShareTop — HangupPenaltyNotice.HpShareTop; null when not penalised.
    public static bool Suspected(int? hpAtDrop, int? maxHpAtDrop, int? hpShareTop)
    {
        if (hpShareTop is not { } share || hpAtDrop is not { } hp) return false;
        if (hp <= 0) return true;
        return maxHpAtDrop is { } max && (long)max * share / 100 >= hp;
    }

    //   hpAtDrop / maxHpAtDrop — as the statline last gave them in the game; null if not known.
    //   hpNow          — HP on the way back in; null until a statline has given it.
    //   livesBefore / livesNow — null when not known; livesNow only from a `stat`
    //                    read on this connection.
    //   roomBefore / roomNow — the room the map was sure of then, and is sure of now.
    //   heldGone       — an item or the coins held then are not held now.
    public static (HangupDeathVerdict Verdict, string Why) Judge(
        int? hpAtDrop, int? maxHpAtDrop, int? hpShareTop, int? hpNow,
        int? livesBefore, int? livesNow, RoomRef? roomBefore, RoomKey? roomNow, bool heldGone)
    {
        if (hpAtDrop is not { } dropHp)
            return (HangupDeathVerdict.NotSuspected, "HP wasn't known when the character left the game");
        if (hpShareTop is null)
            return (HangupDeathVerdict.NotSuspected, "the realm's settings don't penalise that hang-up");
        if (!Suspected(hpAtDrop, maxHpAtDrop, hpShareTop))
            return (HangupDeathVerdict.NotSuspected,
                $"HP was {dropHp}, more than the {hpShareTop}% of max HP the realm's settings say the penalty takes at most");

        if (hpNow is not { } hp)
            return (HangupDeathVerdict.Unsure, "HP isn't known yet");
        if (hp <= 0 || hp <= dropHp)
            return (HangupDeathVerdict.Alive,
                $"HP is {hp} where it was {dropHp}: a death would have set it to its maximum");

        if (livesBefore is { } before && livesNow is { } now)
        {
            if (now == before - 1)
                return (HangupDeathVerdict.Died, $"HP is {hp} where it was {dropHp}, and lives went from {before} to {now}");
            // More than one life down is more than this hang-up can have cost.
            return now < before
                ? (HangupDeathVerdict.Unsure,
                    $"lives went from {before} to {now}, more than the one a hang-up costs; HP is {hp} where it was {dropHp}")
                : (HangupDeathVerdict.Alive,
                    $"lives read {before} before and {now} now, so no life was lost; HP is {hp} where it was {dropHp}");
        }

        string lives = livesBefore is null ? "lives weren't known before" : "no `stat` has given the lives since";
        if (roomBefore is not { } left || roomNow is not { } here)
            return (HangupDeathVerdict.Unsure,
                $"HP is {hp} where it was {dropHp}, but {lives} and the map isn't sure of both rooms");
        if (here.Map == left.Map && here.Room == left.Room)
            return (HangupDeathVerdict.Unsure,
                $"HP is {hp} where it was {dropHp}, but the character is in the room it left the game in and {lives}");
        return heldGone
            ? (HangupDeathVerdict.Died,
                $"HP is {hp} where it was {dropHp}, the character is at {here}, not at {left.Map}/{left.Room} where it "
                + $"left the game, and what it held is gone ({lives})")
            : (HangupDeathVerdict.Unsure,
                $"HP is {hp} where it was {dropHp} and the character is at {here}, not at {left.Map}/{left.Room}, but "
                + $"nothing it held is gone and {lives}");
    }

    // The pile for the record: what was held when the character left the game and
    // isn't now, the worn pieces with their slots and the rest by name (a stack as
    // the inventory list words it, "3 torch"). What stayed with the character
    // (loyal and cursed items) is still held, so it isn't on it.
    public static (List<DeathItem> Equipped, List<DeathItem> Lost) Pile(HeldAtDisconnect before, InventorySnapshot now)
    {
        Dictionary<string, int> missing = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = new();
        foreach ((string name, int count) in HangupItemPlan.Missing(before.Items, now))
        {
            missing[name] = count;
            order.Add(name);
        }

        List<DeathItem> equipped = new();
        foreach (DeathItem worn in before.Worn ?? new List<DeathItem>())
        {
            if (missing.GetValueOrDefault(worn.Name) <= 0) continue;
            missing[worn.Name]--;
            equipped.Add(new DeathItem(worn.Name, worn.Slot));
        }

        List<DeathItem> lost = new();
        foreach (string name in order)
            if (missing[name] is > 0 and int left)
                lost.Add(new DeathItem(left > 1 ? $"{left} {name}" : name));
        return (equipped, lost);
    }
}
