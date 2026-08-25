using System.Collections.Generic;

namespace MudPlay.Models.Profile;

// Root DTO for Data/BBS/{bbs}/roomba.json — Roomba Mode's BBS-tier settings.
// A BBS ties to one game-data set and every character on it shares the same
// gang house, so room labels and the sweep-tuning knobs are board-wide rather
// than per-character: label a room once on any character and it's there
// (and its floor is queryable via @roomba) for every other character on that
// BBS. See GhRoomLabelStore.
public sealed class RoombaSettings
{
    // Labeled gang-house rooms and their sort rules. null/empty = GH not set
    // up yet on this BBS.
    public List<GhRoomLabel>? RoomLabels { get; set; }

    // Per-room hidden-search count — null = GhRoomLabelStore's default (3).
    public int? SearchesPerRoom { get; set; }

    // Whether recon searches (`sea`) each room for hidden items during recon.
    // null/false = sort visible floor items only.
    public bool? SearchForHidden { get; set; }

    // Whether the @roomba remote command replies with an item's last-seen
    // room. Off by default — the feature is opt-in per BBS.
    public bool ResponsesEnabled { get; set; }
}
