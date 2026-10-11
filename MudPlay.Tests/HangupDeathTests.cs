using System.IO;
using MudPlay.Game;
using MudPlay.Game.Health;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A board that penalises a hang-up kills a dropped character, and a standing one
// its HP share takes under the death threshold, in the call that takes it out of
// the game. Nobody sees that death; the client works it out on the next entry.
// These pin the rule it goes by, the pile it builds, and the record it makes.
public sealed class HangupDeathTests
{
    private static readonly RoomRef Left = new(1, 3);
    private static readonly RoomKey Temple = new(1, 1);

    // ----- The realm's settings ----------------------------------------

    [Theory]
    // master, pvpFrom, pvpTo, pve ticked, pveFrom, pveTo, in a PvP fight → the top share
    [InlineData(false, 25, 50, true, 25, 50, true, null)]    // master switch off
    [InlineData(false, 25, 50, true, 25, 50, false, null)]
    [InlineData(true, 25, 50, false, 10, 20, true, 50)]      // PvP fight: the PvP side
    [InlineData(true, 25, 50, false, 10, 20, false, null)]   // not in PvP, PvE not ticked
    [InlineData(true, 25, 50, true, 10, 20, false, 20)]      // PvE ticked: its own figures
    [InlineData(true, 25, 50, true, 10, 20, true, 50)]       // a PvP fight outranks it
    [InlineData(true, 0, 0, true, 0, 0, false, 0)]           // penalised, no HP taken
    [InlineData(true, 60, 30, false, 0, 0, true, 60)]        // the top is never under the bottom
    public void HpShareTop_IsTheSideThatApplied_OrNothingWhenThatHangUpIsFree(
        bool master, int pvpFrom, int pvpTo, bool pve, int pveFrom, int pveTo, bool pvpFight, int? expected)
    {
        RealmProfile realm = new()
        {
            HangupPenaltyEnabled = master,
            HangupPvpHpFromPercent = pvpFrom,
            HangupPvpHpToPercent = pvpTo,
            HangupPvePenaltyEnabled = pve,
            HangupPveHpFromPercent = pveFrom,
            HangupPveHpToPercent = pveTo,
        };

        Assert.Equal(expected, HangupPenaltyNotice.HpShareTop(realm, pvpFight, inFight: true));
        Assert.Null(HangupPenaltyNotice.HpShareTop(null, pvpFight, inFight: true));
    }

    // The board's three levels: only PvP, only while attacked, every hang-up. A
    // hang-up outside any fight is penalised only on a realm set as the last.
    [Theory]
    // PvE ticked, every hang-up ticked, in a PvP fight, in a fight with a monster → the top share
    [InlineData(true, false, false, false, null)]   // attacked-only board, and nothing was attacking
    [InlineData(true, false, false, true, 20)]
    [InlineData(true, true, false, false, 20)]      // every hang-up: at the PvE figures
    [InlineData(true, true, false, true, 20)]
    [InlineData(false, true, false, false, null)]   // the box means nothing without the PvE side
    [InlineData(false, true, false, true, null)]
    [InlineData(true, false, true, false, 50)]      // a PvP fight is penalised on every level
    [InlineData(false, false, true, false, 50)]
    public void HpShareTop_OutsideAFight_OnlyOnARealmThatPenalisesEveryHangUp(
        bool pve, bool everyHangUp, bool pvpFight, bool inFight, int? expected)
    {
        RealmProfile realm = new()
        {
            HangupPenaltyEnabled = true,
            HangupPvpHpFromPercent = 25,
            HangupPvpHpToPercent = 50,
            HangupPvePenaltyEnabled = pve,
            HangupPveHpFromPercent = 10,
            HangupPveHpToPercent = 20,
            HangupOutsideFightPenaltyEnabled = everyHangUp,
        };

        Assert.Equal(expected, HangupPenaltyNotice.HpShareTop(realm, pvpFight, inFight));
    }

    // ----- Whether to look ----------------------------------------------

    [Theory]
    [InlineData(-5, 200, 50, true)]      // dropped
    [InlineData(0, 200, 0, true)]        // dropped, and a side that takes no HP still kills it
    [InlineData(100, 200, 50, true)]     // the largest share reaches 0
    [InlineData(101, 200, 50, false)]
    [InlineData(40, null, 50, false)]    // standing, max HP not known: can't say
    [InlineData(-5, null, 50, true)]
    [InlineData(-5, 200, null, false)]   // that hang-up isn't penalised
    [InlineData(null, 200, 50, false)]   // HP not known when it left
    public void Suspected_OnlyWhenPenalisedAndDroppedOrLowEnoughToBeDropped(
        int? hp, int? maxHp, int? share, bool expected) =>
        Assert.Equal(expected, HangupDeath.Suspected(hp, maxHp, share));

    // ----- The verdict ---------------------------------------------------

    // Left dropped at -5 of 200 with 7 lives on a realm taking up to 50%; back at
    // full HP with 6, nothing worn, the board's hang-up lines printed.
    private static HangupDeathVerdict Verdict(
        int? hpAtDrop = -5, int? share = 50, int? hpAtEntry = 200, int? livesBefore = 7, int? livesNow = 6,
        bool? worn = false, bool? loginLines = true, bool? takenStillHeld = false) =>
        HangupDeath.Judge(hpAtDrop, 200, share, hpAtEntry, livesBefore, livesNow, worn, loginLines, takenStillHeld).Verdict;

    // A life lost says "died": exactly one, both counts read from the game. These
    // give no room it came back in, so the lives speak alone (the room's part is
    // under "The room it came back in" below).
    [Fact]
    public void ExactlyOneLifeFewer_IsADeath()
    {
        Assert.Equal(HangupDeathVerdict.Died, Verdict());
        // A standing character the share finished.
        Assert.Equal(HangupDeathVerdict.Died, Verdict(hpAtDrop: 40));
        // HP not read at the first prompt: the lives still say it.
        Assert.Equal(HangupDeathVerdict.Died, Verdict(hpAtEntry: null));
        // Where a death isn't known to unequip, or the board to print its lines
        // (not Stock), neither is asked for.
        Assert.Equal(HangupDeathVerdict.Died, Verdict(worn: null, loginLines: null));
        // The list held nothing a death takes, or what it held isn't known: nothing
        // to go by there.
        Assert.Equal(HangupDeathVerdict.Died, Verdict(takenStillHeld: null));
    }

    // Left at the top of its HP on a realm whose share goes to 100%: a death sets
    // HP to the maximum, which is where it already was.
    [Fact]
    public void ADeathFromFullHp_ShowsNoRiseInHp_AndIsStillADeath() =>
        Assert.Equal(HangupDeathVerdict.Died,
            HangupDeath.Judge(200, 200, 100, 200, 7, 6, worn: false, loginLines: true, takenStillHeld: false).Verdict);

    [Fact]
    public void NotLookedAt_WhenNotPenalised_NotLowEnough_OrHpWasNotKnown()
    {
        Assert.Equal(HangupDeathVerdict.NotSuspected, Verdict(share: null));
        Assert.Equal(HangupDeathVerdict.NotSuspected, Verdict(hpAtDrop: 150));
        Assert.Equal(HangupDeathVerdict.NotSuspected, Verdict(hpAtDrop: null));
    }

    [Fact]
    public void NoLifeLost_IsNoDeath_WhateverElseIsSeen()
    {
        Assert.Equal(HangupDeathVerdict.Alive, Verdict(livesNow: 7));
        // Trained since, with no `stat` read after it: no life was lost.
        Assert.Equal(HangupDeathVerdict.Alive, Verdict(livesNow: 9));
        // Two down is more than this hang-up cost: something else went unseen.
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(livesNow: 5));
    }

    // HP and the pack never say "died". Without the lives now, a `stat` is what is
    // needed; without the lives from before, and with no room it came back in to
    // go by (none is given here), nothing will tell.
    [Fact]
    public void WithoutBothCounts_AndNoRoomToGoBy_NothingSaysDied()
    {
        Assert.Equal(HangupDeathVerdict.NeedsLives, Verdict(livesNow: null));
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(livesBefore: null));
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(livesBefore: null, livesNow: null));
    }

    // A death sets HP to its maximum. Back dropped, or under what it left with,
    // it wasn't killed: no `stat` is needed to say so.
    [Theory]
    [InlineData(-5, -5)]     // a free room: still dropped
    [InlineData(-5, 0)]
    [InlineData(40, -20)]    // the share dropped it, short of the threshold
    [InlineData(40, 12)]     // the share was taken, and it stands
    [InlineData(40, 40)]     // nothing was taken, and a death would have filled it
    public void HpAtEntryOfACharacterNotKilled_ClosesTheQuestion_WhileNoLifeIsKnownLost(int hpAtDrop, int hpAtEntry)
    {
        Assert.Equal(HangupDeathVerdict.Alive, Verdict(hpAtDrop: hpAtDrop, hpAtEntry: hpAtEntry, livesNow: null));
        Assert.Equal(HangupDeathVerdict.Alive, Verdict(hpAtDrop: hpAtDrop, hpAtEntry: hpAtEntry, livesBefore: null));
        Assert.Equal(HangupDeathVerdict.Alive, Verdict(hpAtDrop: hpAtDrop, hpAtEntry: hpAtEntry, livesNow: 7));
    }

    // A life can be lost on another client between two sessions of this one. What
    // the entry showed then takes the death away again: it is told, not recorded.
    [Fact]
    public void ALifeLost_ButNotToThisHangUp_IsNotADeathHere()
    {
        // Something is still worn: a death takes everything off.
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(worn: true));
        // The board didn't print its hang-up lines: another session came in between.
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(loginLines: false));
        // It came back dropped, or under the HP it left with.
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(hpAtEntry: -3));
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(hpAtDrop: 40, hpAtEntry: 12));
        // Something a death takes is still held: it takes all but what stays with
        // the character. This one holds on both realms, with nothing else to go by.
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(takenStillHeld: true));
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(worn: null, loginLines: null, takenStillHeld: true));
    }

    // A hang-up known not to have killed, lives aside: the lives the character
    // left with are then still its lives, and a later list may carry them.
    [Theory]
    [InlineData(20, 50, -10, true)]     // came back dropped
    [InlineData(60, 50, 20, true)]      // came back under what it left with
    [InlineData(60, 50, 60, true)]      // came back as it left, short of full
    [InlineData(150, 50, 200, true)]    // too much HP for the share to drop
    [InlineData(60, null, 200, true)]   // not penalised
    [InlineData(-5, 50, 200, false)]    // dropped, and back at full HP: it may have died
    [InlineData(60, 50, 200, false)]
    [InlineData(60, 50, null, false)]   // HP at the first prompt not read
    [InlineData(null, 50, 20, false)]   // HP at the drop not known
    public void RuledOut_WhenTheHangUpCouldNotKill_OrHpAtTheNextPromptSaysItDidNot(
        int? hpAtDrop, int? share, int? hpAtEntry, bool expected) =>
        Assert.Equal(expected, HangupDeath.RuledOut(hpAtDrop, 200, share, hpAtEntry));

    // Stock's word on the way in that the last exit was a hang-up it didn't let go
    // free, as the engine prints it.
    [Theory]
    [InlineData("Last time you were on, you disconnected while playing.", true)]
    [InlineData("The gods have punished you appropriately.", false)]
    [InlineData("Borric gossips: Last time you were on, you disconnected while playing.", false)]
    public void TheBoardsHangUpLine_IsRecognised(string line, bool recognised)
    {
        MessageRouter router = new();
        MudPlay.Services.Patterns.DefaultPatterns.Seed(router);
        int seen = 0;
        using IDisposable sub = router.Subscribe(MudPlay.Services.Patterns.KnownPatterns.HangupLoginNotice, _ => seen++);

        router.Dispatch(new MudPlay.Terminal.LineExtractor.EmittedLine(
            line, Array.Empty<MudPlay.Terminal.CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        Assert.Equal(recognised ? 1 : 0, seen);
    }

    // ----- The room it came back in --------------------------------------
    //
    // The first room confirmed on the way back in, before the character has moved
    // (HangupDeathArrivalRooms.EntrySays): true where the dead of the room it hung
    // up in arrive, false where a death by this hang-up would not have left it.

    private const string InTheTemple =
        "Temple, Halls of the Dead (1/2189), where the dead of Darkwood Forest (1/109) arrive";

    private static (HangupDeathVerdict Verdict, string Why) RoomVerdict(
        bool? entryRoom, string? entryRoomText = InTheTemple, int? hpAtEntry = 200, int? livesBefore = 7, int? livesNow = 6,
        bool? worn = false, bool? loginLines = true, bool? takenStillHeld = false) =>
        HangupDeath.Judge(-5, 200, 50, hpAtEntry, livesBefore, livesNow, worn, loginLines, takenStillHeld,
            entryRoom, entryRoomText);

    // The owner's example: hung up low, back in the Halls of the Dead, a life down.
    [Fact]
    public void ALifeFewer_BackWhereItsDeadArrive_IsADeath_AndTheWordingNamesTheRoom()
    {
        (HangupDeathVerdict verdict, string why) = RoomVerdict(entryRoom: true);

        Assert.Equal(HangupDeathVerdict.Died, verdict);
        Assert.Equal($"lives went from 7 to 6, and it came back in {InTheTemple}", why);
    }

    // The room is one more mark that must agree: a life lost by a character that
    // comes back where it left, or in a room its dead don't arrive in, was lost
    // somewhere else.
    [Fact]
    public void ALifeFewer_BackInARoomADeathWouldNotLeaveItIn_IsNotThisHangUps()
    {
        (HangupDeathVerdict verdict, string why) =
            RoomVerdict(entryRoom: false, entryRoomText: "Darkwood Forest (1/109), the room it left the game in");

        Assert.Equal(HangupDeathVerdict.Unsure, verdict);
        Assert.Contains("a life was lost (lives 7 to 6)", why);
        Assert.Contains("it came back in Darkwood Forest (1/109), the room it left the game in", why);
        Assert.Contains("lost somewhere else", why);
    }

    // A room that says nothing leaves the lives rule as it was.
    [Fact]
    public void ARoomThatSaysNothing_ChangesNoVerdict()
    {
        Assert.Equal(HangupDeath.Judge(-5, 200, 50, 200, 7, 6, false, true, false),
            RoomVerdict(entryRoom: null, entryRoomText: null));
        Assert.Equal(HangupDeath.Judge(-5, 200, 50, 200, null, 6, false, true, false),
            RoomVerdict(entryRoom: null, entryRoomText: "room 1/110", livesBefore: null));
    }

    // With no count from before the hang-up, the room says it when the character
    // also came back stripped of what a death takes.
    [Fact]
    public void LivesNotReadBefore_BackStrippedWhereItsDeadArrive_IsADeath()
    {
        (HangupDeathVerdict verdict, string why) = RoomVerdict(entryRoom: true, livesBefore: null);

        Assert.Equal(HangupDeathVerdict.Died, verdict);
        Assert.Contains($"it came back in {InTheTemple}", why);
        Assert.Contains("its lives weren't read on the connection it left the game on", why);
        // Not Stock: no login lines to ask for, and a death isn't known to unequip.
        Assert.Equal(HangupDeathVerdict.Died,
            RoomVerdict(entryRoom: true, livesBefore: null, worn: null, loginLines: null).Verdict);
        // The record carries the lives it has now, so a `stat` is still waited for.
        Assert.Equal(HangupDeathVerdict.NeedsLives, RoomVerdict(entryRoom: true, livesBefore: null, livesNow: null).Verdict);
    }

    // The room doesn't record a death against what the pack says: a character
    // that walked to the temple on another client and left the game there still
    // holds its gear. Nor where what it held isn't known.
    [Theory]
    [InlineData(true, false, true)]     // something a death takes is still held
    [InlineData(null, false, true)]     // what it held isn't known
    [InlineData(false, true, true)]     // something is still worn
    [InlineData(false, false, false)]   // Stock, and the board's lines weren't printed
    public void LivesNotReadBefore_TheRoomAlone_IsNotEnough(bool? takenStillHeld, bool? worn, bool? loginLines)
    {
        (HangupDeathVerdict verdict, string why) = RoomVerdict(entryRoom: true, livesBefore: null, worn: worn,
            loginLines: loginLines, takenStillHeld: takenStillHeld);

        Assert.Equal(HangupDeathVerdict.Unsure, verdict);
        Assert.Contains($"it came back in {InTheTemple}", why);
        Assert.Contains("what it holds doesn't bear a death out", why);
    }

    // A character that owned nothing a death takes (no such item, no coin) has
    // nothing to be stripped of. For it the room is enough (the owner,
    // 2026-10-10), and every "no" that still applies is kept.
    [Fact]
    public void LivesNotReadBefore_OwningNothingADeathTakes_TheRoomIsEnough()
    {
        (HangupDeathVerdict Verdict, string Why) Naked(
            bool? entryRoom = true, int? hpAtEntry = 200, int? livesNow = 6, bool? worn = false, bool? loginLines = true) =>
            HangupDeath.Judge(-5, 200, 50, hpAtEntry, livesBefore: null, livesNow, worn, loginLines,
                takenStillHeld: null, entryRoom, InTheTemple, ownedNothing: true);

        (HangupDeathVerdict verdict, string why) = Naked();
        Assert.Equal(HangupDeathVerdict.Died, verdict);
        Assert.Contains($"it came back in {InTheTemple}, and held nothing a death takes when it left", why);
        // The record still needs a count read on this connection.
        Assert.Equal(HangupDeathVerdict.NeedsLives, Naked(livesNow: null).Verdict);

        // What still says no: the room, HP at the first prompt, a piece worn,
        // the board's lines on Stock.
        Assert.Equal(HangupDeathVerdict.Unsure, Naked(entryRoom: null).Verdict);
        Assert.Equal(HangupDeathVerdict.Alive, Naked(entryRoom: false).Verdict);
        Assert.Equal(HangupDeathVerdict.Alive, Naked(hpAtEntry: -5).Verdict);
        Assert.Equal(HangupDeathVerdict.Unsure, Naked(worn: true).Verdict);
        Assert.Equal(HangupDeathVerdict.Unsure, Naked(loginLines: false).Verdict);
        // With both counts read the lives decide, as for anyone.
        Assert.Equal(HangupDeathVerdict.Alive,
            HangupDeath.Judge(-5, 200, 50, 200, 7, 7, false, true, null, true, InTheTemple, ownedNothing: true).Verdict);
    }

    // With no life known lost, a room a death wouldn't have left it in closes the
    // question, as HP at the first prompt does.
    [Theory]
    [InlineData(null, 6)]
    [InlineData(null, null)]
    [InlineData(7, null)]
    public void BackInARoomADeathWouldNotLeaveItIn_ClosesTheQuestion_WhileNoLifeIsKnownLost(int? livesBefore, int? livesNow)
    {
        (HangupDeathVerdict verdict, string why) =
            RoomVerdict(entryRoom: false, entryRoomText: "Darkwood Forest (1/110)", livesBefore: livesBefore, livesNow: livesNow);

        Assert.Equal(HangupDeathVerdict.Alive, verdict);
        Assert.Contains("it came back in Darkwood Forest (1/110), which is not where a death by this hang-up puts it", why);
    }

    // The room can't make a death of what HP at the first prompt rules out.
    [Fact]
    public void BackWhereItsDeadArrive_ButStillDropped_IsNoDeath()
    {
        Assert.Equal(HangupDeathVerdict.Alive, RoomVerdict(entryRoom: true, hpAtEntry: -5, livesBefore: null).Verdict);
        Assert.Equal(HangupDeathVerdict.Unsure, RoomVerdict(entryRoom: true, hpAtEntry: -5).Verdict);
    }

    // ----- Dropped in an arena room ---------------------------------------
    //
    // A hang-up while dropped in an arena room is the arena's death: HP put back,
    // the character moved to where that room's dead arrive, and no life or item
    // taken. So the lives never say it, and only the room it comes back in does.

    private const string InTheArenaHalls =
        "Training Grounds, Halls of the Dead (16/542), where the dead of Training Grounds (16/500) arrive";

    // penalised: the realm's settings penalise that hang-up, so an ordinary death
    // is possible too (the board's arenas switched off).
    private static (HangupDeathVerdict Verdict, string Why) ArenaVerdict(
        int? hpAtDrop = -5, int? hpAtEntry = 150, int? livesBefore = 7, int? livesNow = 7, bool? takenStillHeld = true,
        bool? entryRoom = true, bool penalised = false) =>
        HangupDeath.JudgeArena(hpAtDrop, hpAtEntry, livesBefore, livesNow, takenStillHeld, entryRoom, InTheArenaHalls,
            ordinaryDeathPossible: penalised);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DroppedInAnArenaRoom_BackWhereItsDeadArrive_IsAnArenaDeath_WithNoLifeLost(bool penalised)
    {
        (HangupDeathVerdict verdict, string why) = ArenaVerdict(penalised: penalised);

        Assert.Equal(HangupDeathVerdict.Died, verdict);
        Assert.Equal($"it left the game dropped in an arena room and came back in {InTheArenaHalls}, its lives still 7", why);
        // The count from before wasn't read: an arena death takes none, so the one
        // read now is the record's. Something a death takes is still held, which an
        // ordinary death would not have left.
        Assert.Equal(HangupDeathVerdict.Died, ArenaVerdict(livesBefore: null, livesNow: 6, penalised: penalised).Verdict);
        // The pack can't say, but the lives are unchanged; HP not read at the first prompt.
        Assert.Equal(HangupDeathVerdict.Died, ArenaVerdict(takenStillHeld: null, hpAtEntry: null, penalised: penalised).Verdict);
    }

    // An arena death costs no life, so it is recorded with no count from before
    // (the owner, 2026-10-10), even when the pack can't say either. The one
    // corner told and not recorded: that hang-up is one the realm penalises, so
    // with the board's arenas off it would have been an ordinary death, and
    // neither the lives nor the pack can tell the two apart.
    [Fact]
    public void AnArenaDeath_WithNoCountAndAPackThatCantSay_IsRecorded_UnlessAnOrdinaryDeathIsPossible()
    {
        Assert.Equal(HangupDeathVerdict.Died,
            ArenaVerdict(livesBefore: null, livesNow: 6, takenStillHeld: null).Verdict);
        Assert.Equal(HangupDeathVerdict.NeedsLives,
            ArenaVerdict(livesBefore: null, livesNow: null, takenStillHeld: null).Verdict);

        (HangupDeathVerdict verdict, string why) =
            ArenaVerdict(livesBefore: null, livesNow: 6, takenStillHeld: null, penalised: true);
        Assert.Equal(HangupDeathVerdict.Unsure, verdict);
        Assert.Contains("this realm penalises that hang-up", why);
        Assert.Contains("doesn't tell an arena death, which takes nothing, from an ordinary one", why);
        Assert.Equal(HangupDeathVerdict.Unsure,
            ArenaVerdict(livesBefore: null, livesNow: null, takenStillHeld: null, penalised: true).Verdict);
    }

    [Fact]
    public void AnArenaDeath_WaitsForALivesCountReadOnThisConnection() =>
        Assert.Equal(HangupDeathVerdict.NeedsLives, ArenaVerdict(livesNow: null).Verdict);

    // Everything that isn't the arena's death is handed back to the ordinary rule.
    [Fact]
    public void NotAnArenaDeath_WhenTheRoomDoesNotSayIt_OrTheMarksAreAnOrdinaryDeaths()
    {
        // Standing when it left: a free hang-up there does nothing to it.
        Assert.Equal(HangupDeathVerdict.NotSuspected, ArenaVerdict(hpAtDrop: 40).Verdict);
        Assert.Equal(HangupDeathVerdict.NotSuspected, ArenaVerdict(hpAtDrop: null).Verdict);
        // Back where it left, somewhere else, or in a room that wasn't confirmed.
        Assert.Equal(HangupDeathVerdict.NotSuspected, ArenaVerdict(entryRoom: false).Verdict);
        Assert.Equal(HangupDeathVerdict.NotSuspected, ArenaVerdict(entryRoom: null).Verdict);
        // Still dropped: the arena's death puts HP back.
        Assert.Equal(HangupDeathVerdict.NotSuspected, ArenaVerdict(hpAtEntry: -5).Verdict);
        // A life gone, or nothing left of what a death takes: an ordinary death,
        // as in an arena room on Stock with the arena switch off.
        Assert.Equal(HangupDeathVerdict.NotSuspected, ArenaVerdict(livesNow: 6).Verdict);
        Assert.Equal(HangupDeathVerdict.NotSuspected, ArenaVerdict(takenStillHeld: false).Verdict);
        Assert.Equal(HangupDeathVerdict.NotSuspected, ArenaVerdict(takenStillHeld: false, livesNow: null).Verdict);
    }

    // ----- The pile ------------------------------------------------------

    private static InventorySnapshot Snap((string Name, string Slot)[]? worn = null, params string[] carried) =>
        new(CurrencyHoldings.Empty, EncumbranceReading.Empty,
            (worn ?? []).Select(w => new EquippedItem(w.Name, w.Slot)).ToList(),
            carried, DateTimeOffset.Now, null, null);

    private static HeldAtDisconnect ListOf(InventorySnapshot held) => new()
    {
        At = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
        Room = Left,
        Items = HangupItemPlan.Held(held),
        Worn = DeathLootCapture.FromSnapshot(held).Equipped,
        Carried = DeathLootCapture.LostOf(held),
    };

    [Fact]
    public void Pile_IsWhatWasHeldAndIsNotNow_WornPiecesWithTheirSlots()
    {
        HeldAtDisconnect before = ListOf(Snap(
            worn: [("chainmail hauberk", "Torso"), ("gold ring", "Finger"), ("gold ring", "Finger"), ("soulbound amulet", "Neck")],
            "3 torch", "rope", "gold ring"));

        // A loyal piece stayed (in the pack now: a death unequips everything), and
        // one torch and one ring are still held.
        (List<DeathItem>? equipped, List<DeathItem>? lost) = HangupDeath.Pile(before,
            Snap(null, "soulbound amulet", "torch", "gold ring"));

        Assert.Equal(new[] { ("chainmail hauberk", "Torso"), ("gold ring", "Finger"), ("gold ring", "Finger") },
            equipped!.Select(i => (i.Name, i.Slot!)).ToArray());
        // The carried stack as the inventory words it, which recovery expands per copy.
        Assert.NotNull(lost);
        Assert.Equal(new[] { "2 torch", "rope" }, lost.Select(i => i.Name).ToArray());
        Assert.All(lost, i => Assert.Null(i.Slot));
    }

    // A lit light and keys drop at a death like everything else, so they are on
    // the pile: the keys as the key ring words them, the lit light by its name.
    [Fact]
    public void Pile_TakesTheKeyRingAndTheLitLightToo()
    {
        InventorySnapshot held = new(CurrencyHoldings.Empty, EncumbranceReading.Empty,
            [new EquippedItem("chainmail hauberk", "Torso")], ["2 torch", "rope"], DateTimeOffset.Now,
            new ReadiedLight("torch", 40), ["2 black star key", "brass key"]);
        HeldAtDisconnect before = ListOf(held);

        (List<DeathItem>? equipped, List<DeathItem>? lost) = HangupDeath.Pile(before, Snap(null, "brass key"));

        Assert.Equal("chainmail hauberk", Assert.Single(equipped!).Name);
        // Three torches in all: the two carried and the lit one. The brass key is
        // still held (in the pack now), so it isn't on it.
        Assert.Equal(new[] { "2 torch", "rope", "2 black star key", "torch" }, lost!.Select(i => i.Name).ToArray());
    }

    // A death that is seen and one worked out afterwards record the same pile for
    // the same inventory: the worn pieces, every pack copy (a spare of a worn piece
    // among them), the keys marked as keys, and the lit light.
    [Fact]
    public void Pile_OfADeathWorkedOutAfterwards_IsThePileADeathThatWasSeenRecords()
    {
        InventorySnapshot held = new(CurrencyHoldings.Empty, EncumbranceReading.Empty,
            [new EquippedItem("longsword", "Weapon Hand"), new EquippedItem("gold ring", "Finger")],
            ["longsword", "3 torch", "rope"], DateTimeOffset.Now,
            new ReadiedLight("torch", 40), ["2 black star key", "brass key"]);

        (List<DeathItem> seenWorn, List<DeathItem> seenLost) = DeathLootCapture.FromSnapshot(held);
        (List<DeathItem>? laterWorn, List<DeathItem>? laterLost) = HangupDeath.Pile(ListOf(held), Snap(null));

        static (string, string?, bool)[] Shape(List<DeathItem> items) =>
            items.Select(i => (i.Name, i.Slot, i.OnKeyRing)).ToArray();
        Assert.Equal(Shape(seenWorn), Shape(laterWorn!));
        Assert.Equal(Shape(seenLost), Shape(laterLost!));
        Assert.Contains(seenLost, i => i is { Name: "longsword", OnKeyRing: false });   // the spare
        Assert.Equal(new[] { "2 black star key", "brass key" },
            laterLost!.Where(i => i.OnKeyRing).Select(i => i.Name).ToArray());
    }

    // The key-ring mark is kept by the list a disconnect saves, which is where a
    // pile worked out afterwards gets it from.
    [Fact]
    public void TheListKeptAtADisconnect_KeepsWhichEntriesWereKeys()
    {
        HeldAtDisconnect before = ListOf(new InventorySnapshot(CurrencyHoldings.Empty, EncumbranceReading.Empty,
            [], ["rope"], DateTimeOffset.Now, null, ["brass key"]));

        string json = System.Text.Json.JsonSerializer.Serialize(before);
        HeldAtDisconnect back = System.Text.Json.JsonSerializer.Deserialize<HeldAtDisconnect>(json)!;

        Assert.Equal(new[] { ("rope", false), ("brass key", true) },
            back.Carried!.Select(i => (i.Name, i.OnKeyRing)).ToArray());
    }

    // No inventory was read on the connection the list was written on: what was
    // held isn't known, and the record says so with no pile where it used to take
    // an older list's.
    [Fact]
    public void Pile_OfAListThatDoesntKnowWhatWasHeld_IsUnknown()
    {
        HeldAtDisconnect before = new() { ItemsUnknown = true };

        (List<DeathItem>? equipped, List<DeathItem>? lost) = HangupDeath.Pile(before, Snap(null, "rope"));

        Assert.Null(equipped);
        Assert.Null(lost);
    }

    // ----- The record ----------------------------------------------------

    private sealed class TrackerHarness : IDisposable
    {
        private const string GraphJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Temple, Halls of the Dead",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "1/3", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "Dark Cave",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;

        private readonly string _root;
        public CharacterProfile Profile { get; } = new() { LastKnownRoom = new RoomRef(1, 1) };
        public RoomTracker Tracker { get; }
        public MovementCoordinator Coordinator { get; } = new();
        public PlayerDeathMovementHalt Halt { get; }
        public int EngineStops { get; private set; }
        public int Observed { get; private set; }
        public int Inferred { get; private set; }
        public int Recorded { get; private set; }
        public List<byte[]> Sent { get; } = new();

        public TrackerHarness(bool profileLoaded = true)
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-hangupdeath-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");

            Tracker = new RoomTracker(graph);
            if (profileLoaded) Tracker.Hydrate(Profile);
            Tracker.PlayerDeathObserved += () => Observed++;
            Tracker.PlayerDeathInferred += () => Inferred++;
            Tracker.UnwitnessedDeathRecorded += () => Recorded++;
            Halt = new PlayerDeathMovementHalt(Tracker, Coordinator);
            Halt.SetEngineStopper(() => EngineStops++);
            Halt.SetWireSender(Sent.Add);
        }

        public void Dispose()
        {
            Halt.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    private static UnwitnessedDeath Unseen() => new(
        Left, new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), LivesRemaining: 6,
        HangupItemRecheck.DeathMessage,
        Equipped: [new DeathItem("chainmail hauberk", "Torso")],
        Lost: [new DeathItem("2 torch"), new DeathItem("rope")],
        Coins: new CurrencyHoldings(0, 0, 25, 0, 0, 2500));

    [Fact]
    public void AnUnwitnessedDeath_GetsTheRecordADeathLineMakes()
    {
        using TrackerHarness h = new();

        DeathRecord? made = h.Tracker.NoteUnwitnessedDeath(Unseen());

        DeathRecord record = Assert.Single(h.Profile.DeathHistory!);
        Assert.Same(record, made);
        Assert.Equal(1, record.RecordNumber);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), record.At);
        Assert.Equal("1/3", record.RoomKeyText);
        Assert.Equal("Dark Cave", record.RoomName);
        Assert.Equal(6, record.LivesRemaining);
        Assert.Equal(HangupItemRecheck.DeathMessage, record.MessageText);
        Assert.Equal(DeathRecoveryStatus.Active, record.Status);
        Assert.Equal("Torso", Assert.Single(record.EquippedAtDeath!).Slot);
        Assert.Equal(new[] { "2 torch", "rope" }, record.LostItems!.Select(i => i.Name).ToArray());
        Assert.Equal(25, record.CoinsAtDeath!.Value.Gold);
        // Nothing was on screen to keep.
        Assert.Null(record.DeathLogFile);
    }

    // An arena death worked out afterwards gets the record a witnessed one does:
    // in the history, nothing lost, written Recovered so nothing sets out for a pile.
    [Fact]
    public void AnUnwitnessedArenaDeath_IsRecordedWithNothingLost_LikeOneThatWasSeen()
    {
        using TrackerHarness h = new();

        DeathRecord made = h.Tracker.NoteUnwitnessedDeath(Unseen() with
        {
            Message = HangupItemRecheck.ArenaDeathMessage,
            LivesRemaining = 7,
            NothingLost = true,
        })!;

        Assert.Same(made, Assert.Single(h.Profile.DeathHistory!));
        Assert.True(made.NothingLost);
        Assert.Equal(DeathRecoveryStatus.Recovered, made.Status);
        Assert.Equal(RoomTracker.ArenaDeathNote, made.RecoveryMessage);
        Assert.Equal(7, made.LivesRemaining);
        Assert.Equal(HangupItemRecheck.ArenaDeathMessage, made.MessageText);
        // Whatever the caller listed, an arena death has no pile.
        Assert.Null(made.EquippedAtDeath);
        Assert.Null(made.LostItems);
        Assert.Null(made.CoinsAtDeath);
        Assert.True(h.Tracker.LastDeathTookNothing);
        // A death all the same: the engines stop, as for one that was seen.
        Assert.Equal(1, h.Inferred);
        Assert.Equal(1, h.Recorded);
        Assert.Equal(1, h.EngineStops);

        // The next one, an ordinary death, is not taken for an arena death.
        h.Tracker.NoteUnwitnessedDeath(Unseen());
        Assert.False(h.Tracker.LastDeathTookNothing);
    }

    // The character is already in the room the game put it in and the map has
    // read it: the position is left alone, and nothing that belongs to the moment
    // of dying is replayed.
    [Fact]
    public void AnUnwitnessedDeath_LeavesThePositionAlone_AndRaisesItsOwnEvent()
    {
        using TrackerHarness h = new();
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);

        h.Tracker.NoteUnwitnessedDeath(Unseen());

        Assert.Equal(1, h.Inferred);
        Assert.Equal(0, h.Observed);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(Temple, h.Tracker.State.CurrentRoom!.Key);
    }

    // A loop the reconnect restarted must not walk a stripped character out of
    // the temple; there is no respawn display to hurry along.
    // A death worked out long after the login (a `stat` typed hours in) finds a
    // character that has played on since: a loop doing fine, buffs cast after the
    // death. It gets its record, and the engines are left running.
    [Fact]
    public void ADeathFoundLate_IsRecorded_AndStopsNothing()
    {
        using TrackerHarness h = new();

        DeathRecord? made = h.Tracker.NoteUnwitnessedDeath(Unseen() with { AtEntry = false });

        Assert.Same(made, Assert.Single(h.Profile.DeathHistory!));
        Assert.Equal(1, h.Recorded);
        Assert.Equal(0, h.Inferred);
        Assert.Equal(0, h.EngineStops);

        h.Tracker.NoteUnwitnessedDeath(Unseen());
        Assert.Equal(2, h.Recorded);
        Assert.Equal(1, h.Inferred);
        Assert.Equal(1, h.EngineStops);
    }

    // A record with no pile: what was held wasn't known.
    [Fact]
    public void AnUnwitnessedDeathWithAnUnknownPile_RecordsNoneAsADeathSeenWithoutAnInventoryDoes()
    {
        using TrackerHarness h = new();

        DeathRecord record = h.Tracker.NoteUnwitnessedDeath(Unseen() with { Equipped = null, Lost = null, Coins = null })!;

        Assert.Null(record.EquippedAtDeath);
        Assert.Null(record.LostItems);
        Assert.Equal("None recorded.", record.EquippedAtDeathText);
    }

    [Fact]
    public void AnUnwitnessedDeath_StopsTheEngines_WithoutTheRespawnResync()
    {
        using TrackerHarness h = new();
        h.Coordinator.AssertGate(MovementCoordinator.UserGate, "user");

        h.Tracker.NoteUnwitnessedDeath(Unseen());
        h.Halt.FireGraveyardResyncForTests();

        Assert.Equal(1, h.EngineStops);
        Assert.False(h.Coordinator.IsGateAsserted(MovementCoordinator.UserGate));
        Assert.Empty(h.Sent);
    }

    // The witnessed path makes the same record through the same code, numbered on.
    [Fact]
    public void AWitnessedDeath_StillRecordsAsBefore_AndNumbersOn()
    {
        using TrackerHarness h = new();
        h.Tracker.AttachInventorySnapshot(() => Snap(worn: [("dagger", "Weapon Hand")], "rope"));

        h.Tracker.NoteUnwitnessedDeath(Unseen());
        h.Tracker.NoteDeath(5, "You have 5 lives left.");

        Assert.Equal(2, h.Profile.DeathHistory!.Count);
        DeathRecord seen = h.Profile.DeathHistory[1];
        Assert.Equal(2, seen.RecordNumber);
        Assert.Equal("1/1", seen.RoomKeyText);
        Assert.Equal("Temple, Halls of the Dead", seen.RoomName);
        Assert.Equal("Weapon Hand", Assert.Single(seen.EquippedAtDeath!).Slot);
        Assert.Equal("rope", Assert.Single(seen.LostItems!).Name);
        Assert.Equal(1, h.Observed);
        Assert.Equal(RoomConfidence.PendingRespawn, h.Tracker.State.Confidence);
    }

    [Fact]
    public void WithNoProfile_NothingIsRecorded()
    {
        using TrackerHarness h = new(profileLoaded: false);

        Assert.Null(h.Tracker.NoteUnwitnessedDeath(Unseen()));
        Assert.Null(h.Profile.DeathHistory);
        Assert.Equal(0, h.Inferred);
        Assert.Equal(0, h.EngineStops);
    }
}
