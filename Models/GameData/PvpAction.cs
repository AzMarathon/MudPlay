namespace MudPlay.Models.GameData;

// What to do about a hostile player — MegaMUD's PvP Action group. One is picked as
// the general response on the PvP settings tab, and a player's record can carry its
// own in place of it.
//   HangUp — disconnect at once.
//   FleeThenHangUp — run first, then disconnect.
//   Flee — run, and come back later.
//   Attack — fight where we stand.
//   ChaseAttack — fight, and follow them if they leave.
//   DoNothing — for a realm without PvP.
public enum PvpAction
{
    HangUp         = 0,
    FleeThenHangUp = 1,
    Flee           = 2,
    Attack         = 3,
    ChaseAttack    = 4,
    DoNothing      = 5,
}
