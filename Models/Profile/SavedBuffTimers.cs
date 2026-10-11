namespace MudPlay.Models.Profile;

// The buff timers the character had when it was last out of the game, each with
// the time it had left, so they outlast a client restart. "buffs survive
// disconnects, relogs", and "we should have buffs that we're tracking persist
// between sessions.. BUT for each buff we're tracking, as soon as we've
// disconnected all timers need to be paused until we're back in the game, then
// they all resume" (user, 2026-10-10).
//
// Written by Game.Spells.BuffTimerKeeper on every profile save made while the
// character is out of the game, and cleared by the first save made back in it: a
// buff's time only stands still while the character is out, so a record written
// in the game would be wrong by however long the client ran on after it. A client
// that died in the game therefore leaves none, and its buffs are raised afresh.
//
// Only buffs on the character itself are kept. A timer on a party member is not:
// after a restart there is no knowing the member was out of the game for the same
// stretch.
public sealed class SavedBuffTimers
{
    // When the timers stopped: the moment the character left the game. Told in the
    // program log and the bug report; how old the record is does not decide whether
    // it is used, since the time out of the game is exactly what is not counted.
    public DateTime FrozenAtUtc { get; set; }

    // The BBS and realm the character was on ("bbs/realm"), and the profile's name.
    // A record from another realm, or carried into another character's profile by a
    // copy, says nothing about the character now loaded and is dropped.
    public string? Realm { get; set; }
    public string? Profile { get; set; }

    // The cast pass's timers on the character: buff slots, whole-party buffs,
    // item-cast buffs and a draw item's card.
    public List<SavedBuffTimer> Buffs { get; set; } = new();

    // Buffs kept up against a room hazard (the desert waterskin).
    public List<SavedHazardBuff> HazardBuffs { get; set; } = new();
}
