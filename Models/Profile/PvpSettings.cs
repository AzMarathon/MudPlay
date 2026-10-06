using MudPlay.Models.GameData;

namespace MudPlay.Models.Profile;

// Settings → PvP, stored in the character profile's "Pvp" section. Only read on a
// realm whose "PvP is enabled on this realm" is ticked.
public sealed class PvpSettings
{
    // What to do about an Enemy who is in the room or attacks us. A player's own
    // PvP response (Game Data → Players) replaces it for that player.
    public PvpAction Action { get; set; } = PvpAction.HangUp;

    // Where a flee goes. Null runs back along the walk or loop instead, RoomsToFlee
    // rooms.
    public RoomRef? FleeTo { get; set; }

    public int RoomsToFlee { get; set; } = 10;

    // "Flee then hang up": seconds between the flee starting and the hang-up.
    public int FleeHangupDelaySeconds { get; set; } = 30;

    // "Flee (come back later)": seconds to stay away once the flee has landed before
    // the interrupted walk or loop is picked up again.
    public int ComeBackAfterSeconds { get; set; } = 60;

    // Tell the gang channel who it was and where, once per encounter.
    public bool NotifyGang { get; set; }

    // Dial back in after a PvP hang-up, this many minutes later. ReconnectEntersRealm
    // off stops at the menu, as the login after any other hang-up does.
    public bool ReconnectAfterPvp { get; set; }
    public int ReconnectAfterPvpMinutes { get; set; } = 30;
    public bool ReconnectEntersRealm { get; set; } = true;

    // After a party member drops out (a teleport split the party), our room attacks
    // are held, and a room attack of theirs isn't read as aimed at us, until they
    // are back in the party or this many seconds have passed.
    public int PartySplitHoldSeconds { get; set; } = 120;

    // A Friend who attacks us is marked Enemy, the way a Neutral who does is. Off,
    // a Friend stays a Friend whatever they do and is never answered.
    public bool FlipFriendToEnemyIfAttacked { get; set; }

    // Attacking a player who hasn't attacked us is an evil deed the game refuses
    // while evil warnings are on. On, the client sends `set warning off` and attacks
    // again.
    public bool TurnOffEvilWarningsToAttack { get; set; }

    // The same, for a leader's @kill naming a player: the warnings go off before
    // the attack, since being ordered onto someone who attacked the leader is still
    // an evil deed for us.
    public bool KillOrderTurnsOffEvilWarnings { get; set; }

    // Warnings we switched off go back on this long after we are back at what the
    // fight interrupted, with no new fight in between.
    public int WarningsBackAfterSeconds { get; set; } = 60;

    // Spells used on a player in a fight. With no combat spell among them, the
    // attack is the combat profile's.
    public PvpSpellSlot Spell1 { get; set; } = new();
    public PvpSpellSlot Spell2 { get; set; } = new();

    // Chase and attack: how many rooms to follow without seeing them before the
    // chase is given up.
    public int ChaseRoomsUnseen { get; set; } = 8;

    // When the way they went wasn't seen, carry on the way they were heading, or
    // take the room's only other way out. Off, the chase waits where it stands.
    public bool ChaseGuessDirection { get; set; } = true;

    // At a crossroads with a door that just opens, how many rooms to look in behind
    // it before coming back to try another way.
    public int ChaseDoorRooms { get; set; } = 3;

    // With no way to follow, how long to stand in case they come back into sight.
    public int ChaseWaitSeconds { get; set; } = 20;

    // Out of sight in a chase, `track` them: on arriving in each room, and on this
    // interval while standing with no way to follow.
    public bool TrackEnemies { get; set; }
    public int TrackEnemiesEverySeconds { get; set; } = 60;
}
