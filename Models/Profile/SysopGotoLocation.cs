namespace MudPlay.Models.Profile;

// One curated destination for the `sys goto` sysop power (BbsCredentials.SysopGotos).
// The client can't tell from the wire command alone where a goto lands — it sends
// only the keyword and the game resolves it — so this table is how the client knows
// the landing room: Name is sent verbatim, Map/Room is where it drops the player.
public sealed class SysopGotoLocation
{
    // The board's own location keyword, sent verbatim after `sys goto`. This is the
    // game's word, not a friendly nickname — menus resolve the room name for display
    // separately (via RoomGraphManager), so there's no second label field.
    public string Name { get; set; } = string.Empty;

    // Where the jump lands, in the same map/room addressing the nav graph keys rooms
    // by (RoomKey). Used to re-anchor position after the jump and — in a later PR —
    // to route through the goto as a shortcut.
    public int Map { get; set; }
    public int Room { get; set; }

    // Optional per-row routing/firing guard: below this level the destination is too
    // dangerous for the character, so menus grey the row out and (PR 2) the router
    // won't take the hop. 0 means ungated. This is a client-side judgement the user
    // encodes, not a game rule — all goto rooms are safe rooms.
    public int MinLevel { get; set; }

    // The starter locations a fresh credential ships with, so the table isn't empty
    // on first use. These are the standard-realm coordinates + sensible level gates
    // (the deeper towns want a few levels first); the user edits or removes them
    // per-BBS, and a renumbered game-data set may need them corrected. A fresh list
    // per call — the rows are mutable and must not be shared between credentials.
    public static List<SysopGotoLocation> DefaultStarterSet() => new()
    {
        new() { Name = "newhaven",  Map = 1,  Room = 2150, MinLevel = 0 },
        new() { Name = "silvermere", Map = 1,  Room = 2189, MinLevel = 0 },
        new() { Name = "rhudaur",    Map = 2,  Room = 2519, MinLevel = 20 },
        new() { Name = "khazarad",   Map = 6,  Room = 1249, MinLevel = 20 },
        new() { Name = "lostcity",   Map = 16, Room = 426,  MinLevel = 40 },
    };
}
