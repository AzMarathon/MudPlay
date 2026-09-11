namespace MudPlay.Models.Profile;

// One move a Roomba sweep hadn't finished when it stopped: where the item was,
// where it was going, and whether it was already in the pack at the time.
// JSON-friendly (public settable properties, "map/room" coordinate strings)
// because it round-trips through CharacterProfile — the whole point is to
// outlive the sweep that made it, including an app restart.
//
// Carried decides what a restore does with it. A carried move is an item sitting
// in the player's pack that only this record knows the destination of, so it's
// re-adopted (after an inventory check) even by a fresh sweep. An uncollected one
// is just planned work, so it's offered to Resume — which skips the scan — and
// ignored by a fresh Start, which re-scans anyway.
public sealed class GhSuspendedMove
{
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Item { get; set; }
    public int Count { get; set; }
    public bool Carried { get; set; }
    public bool Hidden { get; set; }

    public GhSuspendedMove() { }

    public GhSuspendedMove(string from, string to, string item, int count, bool carried, bool hidden)
    {
        From = from;
        To = to;
        Item = item;
        Count = count;
        Carried = carried;
        Hidden = hidden;
    }
}
