using MudPlay.Game.Inventory;
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
// A death record sends Death Recovery walking for a pile, stops the engines and
// clears the buff timers, so a wrong one costs far more than a missing one. Only
// one thing says "died": exactly one life fewer than the character left the game
// with, both counts read from the game. Lives go down by nothing but a death.
// HP, the room and the pack never say it: all three change by playing on, and
// the list they are compared with can be older than the hang-up.
//
// What else is seen can only take a death away again. A life can be lost on
// another client between two sessions of this one, and that death is not this
// hang-up's. A death by this hang-up leaves marks the entry shows:
//   - HP at the first prompt is above 0 and not under what the character left
//     with: a death sets it to the maximum.
//   - something it held is gone: a death takes everything but what stays with
//     the character, on both realms (to the floor on Stock, into a corpse on
//     Paradigm). A character that still holds all of it died somewhere else and
//     got it back.
//   - nothing is worn (Stock): a death takes every piece off the body.
//   - the board's two login lines were printed (Stock): they follow the first
//     entry after a hang-up it didn't let go free, and no other.
// A life lost with one of those missing is not recorded, and is told.
//
// One case nothing on the connection separates: the character was played from
// another client in between and that session itself ended in a death by a
// hang-up. It comes back stripped, a life down, on Stock with the board's lines
// printed. The record made then names where this client last had the character,
// which is why its wording says exactly that and no more.
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
    //   hpAtEntry      — HP at the first prompt of this connection; null if none was read.
    //   livesBefore    — the count the character left the game with, read on that
    //                    connection; null when it wasn't.
    //   livesNow       — from a `stat` read on this connection; null until one is.
    //   worn           — something was worn at the first inventory read of this
    //                    connection; null where a death isn't known to unequip
    //                    (not Stock) or no inventory has been read.
    //   loginLines     — the board's hang-up lines were printed on this connection;
    //                    null where it isn't known to print them (not Stock).
    //   heldGone       — an item or coins the list has were not held at the first
    //                    inventory read; null when the list held nothing, what it
    //                    held isn't known, or no inventory has been read.
    public static (HangupDeathVerdict Verdict, string Why) Judge(
        int? hpAtDrop, int? maxHpAtDrop, int? hpShareTop, int? hpAtEntry,
        int? livesBefore, int? livesNow, bool? worn, bool? loginLines, bool? heldGone)
    {
        if (hpAtDrop is not { } dropHp)
            return (HangupDeathVerdict.NotSuspected, "HP wasn't known when the character left the game");
        if (hpShareTop is null)
            return (HangupDeathVerdict.NotSuspected, "the realm's settings don't penalise that hang-up");
        if (!Suspected(hpAtDrop, maxHpAtDrop, hpShareTop))
            return (HangupDeathVerdict.NotSuspected,
                $"HP was {dropHp}, more than the {hpShareTop}% of max HP the realm's settings say the penalty takes at most");

        // A death sets HP to its maximum, so one that came back dropped, or under
        // what it left with, wasn't killed by this hang-up.
        string? hpSaysNo = hpAtEntry switch
        {
            { } hp when hp <= 0 => $"it came back dropped (HP {hp}), where a death sets HP to its maximum",
            { } hp when hp < dropHp => $"it came back at {hp} HP, under the {dropHp} it left with, where a death sets HP to its maximum",
            _ => null,
        };

        if (livesBefore is not { } before)
            return hpSaysNo is not null
                ? (HangupDeathVerdict.Alive, hpSaysNo)
                : (HangupDeathVerdict.Unsure,
                    "its lives weren't read on the connection it left the game on, and only a life lost tells a death");
        if (livesNow is not { } now)
            return hpSaysNo is not null
                ? (HangupDeathVerdict.Alive, hpSaysNo)
                : (HangupDeathVerdict.NeedsLives, "no `stat` has given the lives on this connection");

        if (now >= before)
            return (HangupDeathVerdict.Alive, $"lives read {before} before and {now} now, so no life was lost");
        if (now < before - 1)
            return (HangupDeathVerdict.Unsure,
                $"lives went from {before} to {now}, more than the one a hang-up costs");

        // One life fewer. What the entry showed can still say it wasn't lost here.
        string lost = $"a life was lost (lives {before} to {now})";
        if (hpSaysNo is not null)
            return (HangupDeathVerdict.Unsure, $"{lost}, but {hpSaysNo}");
        if (heldGone == false)
            return (HangupDeathVerdict.Unsure,
                $"{lost}, but everything the character held when it left the game is still held, where a death takes "
                + "all but what stays with it: the life was lost somewhere else (on another client?)");
        if (worn == true)
            return (HangupDeathVerdict.Unsure,
                $"{lost}, but something was still worn on entering the game, where a death takes everything off");
        if (loginLines == false)
            return (HangupDeathVerdict.Unsure,
                $"{lost}, but the board didn't print its hang-up lines at this entry, so the character has been in the "
                + "game since (on another client?) and the life wasn't lost to this hang-up");
        return (HangupDeathVerdict.Died, $"lives went from {before} to {now}");
    }

    // The pile for the record, as a death line's record has it: the worn pieces
    // with their slots and the carried items as the inventory list words them (a
    // stack as "3 torch"), less whatever is still held. What stayed with the
    // character (loyal and cursed items) is still held, so it isn't on it; keys
    // and the lit light aren't on a witnessed pile either.
    public static (List<DeathItem> Equipped, List<DeathItem> Lost) Pile(HeldAtDisconnect before, InventorySnapshot now)
    {
        Dictionary<string, int> missing = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int count) in HangupItemPlan.Missing(before.Items, now))
            missing[name] = count;

        List<DeathItem> equipped = new();
        foreach (DeathItem worn in before.Worn ?? new List<DeathItem>())
        {
            if (missing.GetValueOrDefault(worn.Name) <= 0) continue;
            missing[worn.Name]--;
            equipped.Add(new DeathItem(worn.Name, worn.Slot));
        }

        List<DeathItem> lost = new();
        foreach (DeathItem carried in before.Carried ?? new List<DeathItem>())
        {
            (int count, string name) = CountedCommand.SplitLeadingCount(carried.Name.Trim());
            int gone = Math.Min(Math.Max(1, count), missing.GetValueOrDefault(name));
            if (gone <= 0) continue;
            missing[name] -= gone;
            lost.Add(new DeathItem(gone > 1 ? $"{gone} {name}" : name));
        }
        return (equipped, lost);
    }
}
