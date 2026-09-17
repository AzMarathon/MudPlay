namespace MudPlay.Models.Profile;

// Client-tracked remaining charges for one limited-use item, keyed by item number on
// the character. Populated ON PARADIGM (which prints "Uses remaining: N" on look) so
// the count is authoritative — read from the game, not derived. UpdatedUtc is when
// Remaining last changed; a rechargeable item whose UpdatedUtc predates the last
// cleanup has restocked to its game-data max (see ItemChargeTracker). Distinct from
// ItemUseRecord, which is the stock-realm counted-uses form.
public sealed record ItemChargeRecord(int Remaining, System.DateTimeOffset UpdatedUtc);
