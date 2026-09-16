namespace MudPlay.Models.Profile;

// Client-tracked use count for one limited-use item, keyed by item number on the
// character. Used ON STOCK realms, which (unlike Paradigm) print no "Uses remaining"
// line — so the client counts the uses it sends and derives remaining = max − Used.
// UpdatedUtc is when Used last changed; a rechargeable item whose UpdatedUtc predates
// the last cleanup has restocked, so its Used resets to 0 (see ItemUseCountTracker).
public sealed record ItemUseRecord(int Used, System.DateTimeOffset UpdatedUtc);
