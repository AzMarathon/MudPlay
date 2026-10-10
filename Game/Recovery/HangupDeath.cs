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
//   - nothing a death takes is still held: it takes every item but those that
//     stay with the character, and every coin, on both realms (to the floor on
//     Stock, into a corpse on Paradigm). A character that still holds even one
//     such item died somewhere else and got its pile back. One consumable used
//     since must not hide that, so it is "any still held", not "all still held".
//   - nothing is worn: a death takes every piece off the body on Stock. On
//     Paradigm a cursed piece that stays with the character isn't known to come
//     off, and is treated as Stock's does (the user's ruling).
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

    // A death sets HP to its maximum, so a character that came back dropped, under
    // what it left with, or at exactly what it left with short of the maximum (the
    // penalty took nothing) wasn't killed by that hang-up. The reason, or null
    // when HP at the first prompt doesn't say.
    private static string? EntryHpSaysNo(int dropHp, int? maxHpAtDrop, int? hpAtEntry) => hpAtEntry switch
    {
        { } hp when hp <= 0 => $"it came back dropped (HP {hp}), where a death sets HP to its maximum",
        { } hp when hp < dropHp => $"it came back at {hp} HP, under the {dropHp} it left with, where a death sets HP to its maximum",
        { } hp when hp == dropHp && maxHpAtDrop is { } max && dropHp < max =>
            $"it came back at the {hp} HP it left with, where a death sets HP to its maximum ({max})",
        _ => null,
    };

    // Whether a hang-up is known not to have killed, lives aside: it couldn't (it
    // wasn't penalised, or HP was too high for the share), or HP at the first
    // prompt after it says so. Then the lives the character left with are still
    // its lives.
    public static bool RuledOut(int? hpAtDrop, int? maxHpAtDrop, int? hpShareTop, int? hpAtEntry) =>
        hpAtDrop is { } dropHp
        && (!Suspected(hpAtDrop, maxHpAtDrop, hpShareTop) || EntryHpSaysNo(dropHp, maxHpAtDrop, hpAtEntry) is not null);

    //   hpAtDrop / maxHpAtDrop — as the statline last gave them in the game; null if not known.
    //   hpAtEntry      — HP at the first prompt of this connection; null if none was read.
    //   livesBefore    — the count the character left the game with, read on that
    //                    connection; null when it wasn't.
    //   livesNow       — from a `stat` read on this connection; null until one is.
    //   worn           — a piece was worn at the first inventory read; null when no
    //                    inventory has been read.
    //   loginLines     — the board's hang-up lines were printed on this connection;
    //                    null where it isn't known to print them (not Stock).
    //   takenStillHeld — an item of the list that a death takes, or coins, was still
    //                    held at the first inventory read; null when the list has
    //                    nothing a death takes, what it held isn't known, or no
    //                    inventory has been read.
    public static (HangupDeathVerdict Verdict, string Why) Judge(
        int? hpAtDrop, int? maxHpAtDrop, int? hpShareTop, int? hpAtEntry,
        int? livesBefore, int? livesNow, bool? worn, bool? loginLines, bool? takenStillHeld)
    {
        if (hpAtDrop is not { } dropHp)
            return (HangupDeathVerdict.NotSuspected, "HP wasn't known when the character left the game");
        if (hpShareTop is null)
            return (HangupDeathVerdict.NotSuspected, "the realm's settings don't penalise that hang-up");
        if (!Suspected(hpAtDrop, maxHpAtDrop, hpShareTop))
            return (HangupDeathVerdict.NotSuspected,
                $"HP was {dropHp}, more than the {hpShareTop}% of max HP the realm's settings say the penalty takes at most");

        string? hpSaysNo = EntryHpSaysNo(dropHp, maxHpAtDrop, hpAtEntry);

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
        if (takenStillHeld == true)
            return (HangupDeathVerdict.Unsure,
                $"{lost}, but something the character held when it left the game is still held, where a death takes "
                + "all but what stays with it: the life was lost somewhere else, and the pile got back (on another client?)");
        if (worn == true)
            return (HangupDeathVerdict.Unsure,
                $"{lost}, but something was still worn on entering the game, where a death takes everything off");
        if (loginLines == false)
            return (HangupDeathVerdict.Unsure,
                $"{lost}, but the board didn't print its hang-up lines at this entry, so the character has been in the "
                + "game since (on another client?) and the life wasn't lost to this hang-up");
        return (HangupDeathVerdict.Died, $"lives went from {before} to {now}");
    }

    // The pile for the record: the worn pieces with their slots and everything
    // else a death takes, as the inventory list words it (a stack as "3 torch"),
    // less whatever is still held. What stayed with the character (loyal and
    // cursed items) is still held, so it isn't on it. Both null when the list
    // doesn't know what was held.
    //
    // The list's two halves were taken by DeathLootCapture when the character left
    // the game, the rule a death that is seen uses, so a pile worked out afterwards
    // is the pile that death would have recorded, less what came back. A key keeps
    // its key-ring mark.
    public static (List<DeathItem>? Equipped, List<DeathItem>? Lost) Pile(HeldAtDisconnect before, InventorySnapshot now)
    {
        if (before.ItemsUnknown) return (null, null);
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
            lost.Add(new DeathItem(gone > 1 ? $"{gone} {name}" : name, onKeyRing: carried.OnKeyRing));
        }
        return (equipped, lost);
    }
}
