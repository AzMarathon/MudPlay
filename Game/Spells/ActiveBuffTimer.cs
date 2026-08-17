namespace MudPlay.Game.Spells;

// A read-only snapshot of one live buff-duration timer from CastingDirector, for
// the Buff Watchdog window. Target "" = self; otherwise the party member's
// given name (lower-cased). Short is the 4-letter cast code (or the item-cast
// token). Until is the wear-off instant (UTC); the buff enters its recast window
// at Until - MarginSec. TotalSec is the buff's full duration, so a progress bar
// can render 0..TotalSec with the recast marker at (TotalSec - MarginSec).
public readonly record struct ActiveBuffTimer(
    string Target,
    string Short,
    System.DateTime Until,
    int MarginSec,
    int TotalSec);
