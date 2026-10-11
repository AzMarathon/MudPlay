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
// clears the buff timers, so a wrong one costs far more than a missing one. Two
// things say "died":
//   - exactly one life fewer than the character left the game with, both counts
//     read from the game. Lives go down by nothing but a death.
//   - where the lives weren't read before the hang-up, the room it comes back in:
//     the first room confirmed on the way in, before it has moved, is where the
//     dead of the room it hung up in arrive, and it hung up somewhere else
//     (HangupDeathArrivalRooms; the owner, 2026-10-10: "we'll know immediately
//     upon logging in"). It must also come back stripped of what a death takes,
//     which is what tells it from a character that walked there on another
//     client; a character that owned nothing a death takes has nothing to be
//     stripped of, and for it the room is enough (the owner, 2026-10-10).
// HP and the pack never say it, and neither does a room the character has walked
// to since: all change by playing on, and the list they are compared with can be
// older than the hang-up.
//
// What else is seen can only take a death away again. A life can be lost on
// another client between two sessions of this one, and that death is not this
// hang-up's. A death by this hang-up leaves marks the entry shows:
//   - the first room confirmed is where its dead arrive. Standing where it left,
//     or anywhere else where the list of those rooms is the world's own
//     (HangupArrivalListTrust), it was not killed here.
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
// A hang-up while dropped in an arena room is its own case, JudgeArena: the death
// it gets there takes no life and nothing else, so it is told by the room alone
// and recorded as an arena death, with no pile.
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
    //   entryRoom      — HangupDeathArrivalRooms.Read for the first room confirmed
    //                    on the way back in: true where the dead of the room it
    //                    hung up in arrive, false where a death by this hang-up
    //                    would not have left it, null when it says nothing.
    //   entryRoomText  — that room and whose dead arrive there, for the wording:
    //                    "Temple, Halls of the Dead (1/2189), where the dead of
    //                    Darkwood Forest (1/109) arrive".
    //   ownedNothing   — the list is known to have held nothing a death takes: no
    //                    such item and no coin. takenStillHeld is null then, with
    //                    nothing to look for.
    public static (HangupDeathVerdict Verdict, string Why) Judge(
        int? hpAtDrop, int? maxHpAtDrop, int? hpShareTop, int? hpAtEntry,
        int? livesBefore, int? livesNow, bool? worn, bool? loginLines, bool? takenStillHeld,
        bool? entryRoom = null, string? entryRoomText = null, bool ownedNothing = false)
    {
        if (hpAtDrop is not { } dropHp)
            return (HangupDeathVerdict.NotSuspected, "HP wasn't known when the character left the game");
        if (hpShareTop is null)
            return (HangupDeathVerdict.NotSuspected, "the realm's settings don't penalise that hang-up");
        if (!Suspected(hpAtDrop, maxHpAtDrop, hpShareTop))
            return (HangupDeathVerdict.NotSuspected,
                $"HP was {dropHp}, more than the {hpShareTop}% of max HP the realm's settings say the penalty takes at most");

        string? hpSaysNo = EntryHpSaysNo(dropHp, maxHpAtDrop, hpAtEntry);
        // A death moves the character to where its dead arrive. Back where it left,
        // or in a room that isn't one of those, it wasn't killed by this hang-up.
        string? roomSaysNo = entryRoom == false
            ? $"it came back in {entryRoomText ?? "a room"}, which is not where a death by this hang-up puts it"
            : null;
        string? saysNo = hpSaysNo ?? roomSaysNo;

        if (livesBefore is not { } before)
        {
            if (saysNo is not null) return (HangupDeathVerdict.Alive, saysNo);
            // No count to compare. The room it came back in says it, when the pack
            // agrees: stripped of what a death takes, nothing worn, and on Stock
            // the board's lines printed. A character that only walked there on
            // another client still holds its gear. One that owned nothing a death
            // takes can't be stripped, and the room is enough for it. The record
            // needs the lives it has now, so a `stat` is still waited for.
            if (entryRoom == true && (takenStillHeld == false || ownedNothing) && worn != true && loginLines != false)
                return livesNow is null
                    ? (HangupDeathVerdict.NeedsLives, "no `stat` has given the lives on this connection")
                    : (HangupDeathVerdict.Died,
                        $"it came back in {entryRoomText ?? "the room its dead arrive in"}, "
                        + (ownedNothing ? "and held nothing a death takes when it left" : "with nothing left of what a death takes")
                        + " (its lives weren't read on the connection it left the game on)");
            return (HangupDeathVerdict.Unsure, entryRoom == true
                ? $"it came back in {entryRoomText ?? "the room its dead arrive in"}, but its lives weren't read on the "
                  + "connection it left the game on and what it holds doesn't bear a death out"
                : "its lives weren't read on the connection it left the game on, and only a life lost tells a death");
        }
        if (livesNow is not { } now)
            return saysNo is not null
                ? (HangupDeathVerdict.Alive, saysNo)
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
        if (roomSaysNo is not null)
            return (HangupDeathVerdict.Unsure, $"{lost}, but {roomSaysNo}: the life was lost somewhere else");
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
        return (HangupDeathVerdict.Died, entryRoom == true && entryRoomText is not null
            ? $"lives went from {before} to {now}, and it came back in {entryRoomText}"
            : $"lives went from {before} to {now}");
    }

    // A hang-up while dropped in an arena room (ArenaDeathRooms). The Stock engine
    // gives a dropped character the arena's free death as it takes it out of the
    // game, on any board, penalty or none: HP put back, the character moved to
    // where that room's dead arrive, and no life, item or coin taken
    // (GAME_MECHANICS "Hang-up / lost carrier", the Colliseum exception). On
    // Paradigm an arena death takes none of those either (the owner, 2026-10-10).
    // So the lives never say it, and only the room it comes back in does: the
    // first room confirmed on the way in is where that arena room's dead arrive.
    //
    // What else is seen can only hand it back to Judge, which NotSuspected means
    // here. A life lost, or nothing left of what a death takes, is the mark of an
    // ordinary death (on Stock the arena switch can be off, and an arena room is
    // then a room like any other). A character still dropped was not killed.
    //
    // An arena death costs no life, so it is recorded without a count from before
    // the hang-up (the owner, 2026-10-10). One corner is told and not recorded:
    // the hang-up was one the realm penalises, so an ordinary death is possible
    // too (an arena room with the board's arenas switched off), and neither mark
    // that would tell the two apart can be read: no count from before to find
    // unchanged, and nothing a death takes to find still held.
    //
    // The record carries a count read on this connection, as Judge's does.
    //   takenStillHeld / entryRoom / entryRoomText — as Judge's.
    //   ordinaryDeathPossible — the realm's settings penalise that hang-up.
    public static (HangupDeathVerdict Verdict, string Why) JudgeArena(
        int? hpAtDrop, int? hpAtEntry, int? livesBefore, int? livesNow, bool? takenStillHeld,
        bool? entryRoom, string? entryRoomText, bool ordinaryDeathPossible)
    {
        if (hpAtDrop is not <= 0)
            return (HangupDeathVerdict.NotSuspected, "the character wasn't dropped when it left the game");
        if (entryRoom != true)
            return (HangupDeathVerdict.NotSuspected, "it didn't come back where that arena room's dead arrive");
        if (hpAtEntry is <= 0)
            return (HangupDeathVerdict.NotSuspected, "it came back dropped");
        if (takenStillHeld == false)
            return (HangupDeathVerdict.NotSuspected, "it came back with nothing left of what a death takes, where an arena death takes nothing");
        // The room says it died. Whether it was the arena's death or an ordinary
        // one, with a life gone, nothing here can say.
        if (ordinaryDeathPossible && livesBefore is null && takenStillHeld is null)
            return (HangupDeathVerdict.Unsure,
                $"it left the game dropped in an arena room and came back in {entryRoomText ?? "the room that arena's dead arrive in"}, "
                + "but this realm penalises that hang-up, its lives weren't read on the connection it left on, and what it "
                + "held then doesn't tell an arena death, which takes nothing, from an ordinary one");
        if (livesNow is not { } now)
            return (HangupDeathVerdict.NeedsLives, "no `stat` has given the lives on this connection");
        if (livesBefore is { } before && now < before)
            return (HangupDeathVerdict.NotSuspected, $"a life was lost (lives {before} to {now}), where an arena death takes none");
        return (HangupDeathVerdict.Died,
            $"it left the game dropped in an arena room and came back in {entryRoomText ?? "the room that arena's dead arrive in"}, "
            + (livesBefore is null ? "its lives not read on the connection it left on" : $"its lives still {now}"));
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
