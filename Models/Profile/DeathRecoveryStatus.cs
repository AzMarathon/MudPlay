namespace FujinTerm.Models.Profile;

// Recovery state of a DeathRecord's deathpile, surfaced in the Workshop DEATH
// section with a stoplight tint.
//   Active — the deathpile was made and we have neither re-entered the room nor
//     recovered anything from it.
//   Partial — we returned to the death room; the corpse is present but not yet
//     recovered (or auto-recover is off and we're waiting on the user).
//   Recovered — we recovered the corpse, or the user manually marked the record
//     recovered.
//   Missing — we returned but the corpse was NOT in the room's "You notice"
//     survey (looted, decayed, or already recovered elsewhere). Terminal like
//     Recovered: auto-recover no longer re-arms it, so it never spam-retries — the
//     user can still Recover Now (if the corpse reappears) or clear the record.
public enum DeathRecoveryStatus
{
    Active,
    Partial,
    Recovered,
    Missing,
}
