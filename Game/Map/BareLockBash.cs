namespace MudPlay.Game.Map;

// Whether a bare `(Key: N)` lock can be bashed: a door or gate can, a key exit
// can't, and the cell doesn't say which it is (GAME_MECHANICS "Locked doors —
// picking, opening and bashing"). BareKeyLockBook knows, from the shipped seed or
// from what the game has answered a bash there.
public enum BareLockBash
{
    // Neither the seed nor the realm's own record knows: a bash is tried where it
    // is safe to.
    Unknown,
    // A door or gate: it bashes at Strength.
    Bashable,
    // A key exit: `bash <dir>` there is an attack, never a bash.
    NotBashable,
}
