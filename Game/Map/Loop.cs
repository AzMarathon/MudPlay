using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MudPlay.Game.Map;

// One saved navigation loop — the user's clicked-room cycle plus optional
// per-waypoint commands. The Navigation right-rail "Loops" section lists
// every loop loaded by LoopManager; double-clicking runs it. Loops are
// per game-data set — a loop saved by one character is usable by every
// character, on any BBS, whose game data is that set.
//
// All loops are circular by definition — they end at the room they started
// in and repeat until the user stops them or the engine recovery gate's
// tier-3 backtrack fails terminally. One-shot "go from A to B" is the
// walker's job; the loop runner has no "Finished" event.
//
// Waypoints is the single source of truth for both the rendered cycle and
// the runtime step list. The runner re-runs BFS at start-time to expand
// the move sequence between consecutive waypoints, so loops automatically
// pick up graph improvements between sessions without re-saving. Commands
// attach to waypoints (fire on arrival) rather than living at arbitrary
// positions in a flat step list — simpler to read on disk and matches the
// natural use case ("rest at this room", "ask barmaid here", etc.).
public sealed class Loop
{
    // Serialised loop schema version.
    //   v1: original (had IsCircular, flat Steps list).
    //   v2: dropped IsCircular, added UserWaypoints + Notes.
    //   v3: current. Collapsed UserWaypoints+Steps into a single Waypoints
    //       list with per-waypoint commands; dropped LastRunAt /
    //       LastModifiedAt.
    // LoopManager.LoadAll upgrades older records in memory on load.
    public int SchemaVersion { get; set; } = 3;

    public required string Name { get; set; }

    // Ordered cycle of waypoints. At runtime the loop runner BFS-fills the
    // move sequence between consecutive entries plus the closing leg back
    // to entry 0. Two-or-more entries required to form a runnable cycle.
    public List<LoopWaypoint> Waypoints { get; set; } = new();

    // Free-form user notes. Empty by default.
    public string Notes { get; set; } = string.Empty;

    // Whether the loaded character has this loop among its favourites: it then
    // appears in the right-click Favorites menus (green), where clicking it starts
    // the loop. The character's choice, kept on its profile (LoopFavoritesStore) and
    // stamped onto the loop by LoopManager; the loop file, shared by everyone on the
    // game data, doesn't carry it.
    [JsonIgnore]
    public bool Favorite { get; set; }

    // The flag the loop file carried while favourites were shared by everyone on
    // the game data. A character from before the move reads it once, as its first
    // list; it is written back as found so each of them finds it.
    [JsonPropertyName("Favorite")]
    public bool SharedFavorite { get; set; }

    // Loop-wide combat setting. When true, the loop only engages hostiles in
    // game-data lair rooms (Room.HasLair) — every other room behaves as if
    // auto-combat were off and is walked through. A per-waypoint DoNotAttack
    // still suppresses even a lair room; a triggered rest still clears any
    // suppressed room so it can rest. Toggled from the Edit Loop window and the
    // "Entire Loop Settings" entry in the nav rail.
    //
    // This and LairEntryDebuff below are the loaded character's, not the loop's: two
    // characters run the same loop file each their own way (user, 2026-10-10). They
    // are kept on the profile (LoopSettingsStore) and stamped on by LoopManager, as
    // Favorite is. On a loop handed to LoopManager.Save they are what the character
    // wants for it, and Save keeps them for the character, not in the file.
    [JsonIgnore]
    public bool OnlyAttackInLairRooms { get; set; }

    // Loop-wide: wait one step short of a lair until the combat profile's pre-attack
    // debuff can be cast, and how (LairEntryDebuffMode). The character's, as above.
    [JsonIgnore]
    public LairEntryDebuffMode LairEntryDebuff { get; set; }

    // The two settings as the loop file holds them, from while they were shared by
    // everyone on the game data. A character that has set neither for this loop runs
    // it with these, so every loop behaves as it did; nothing a character changes is
    // written here, and they are written back as found so the others still find them.
    [JsonPropertyName("OnlyAttackInLairRooms")]
    public bool SharedOnlyAttackInLairRooms { get; set; }

    [JsonPropertyName("LairEntryDebuff")]
    public LairEntryDebuffMode SharedLairEntryDebuff { get; set; }

    // The pair above as one value, and the character's pair.
    [JsonIgnore]
    public LoopOwnSettings SharedSettings
    {
        get => new(SharedOnlyAttackInLairRooms, SharedLairEntryDebuff);
        set => (SharedOnlyAttackInLairRooms, SharedLairEntryDebuff) = (value.OnlyAttackInLairRooms, value.LairEntryDebuff);
    }

    [JsonIgnore]
    public LoopOwnSettings OwnSettings
    {
        get => new(OnlyAttackInLairRooms, LairEntryDebuff);
        set => (OnlyAttackInLairRooms, LairEntryDebuff) = (value.OnlyAttackInLairRooms, value.LairEntryDebuff);
    }

    // Folder this loop lives under inside the BBS Loops directory, relative
    // to it, using / separators (e.g. "Sewers/Lower"). Empty = the Loops
    // root. Not serialised — the on-disk subdirectory is the source of
    // truth; LoopManager sets this from the file's location on load and
    // writes the file into the matching subdirectory on save.
    [JsonIgnore]
    public string Folder { get; set; } = string.Empty;

    // Catches old-schema fields on load so LoopManager.LoadAll can run the
    // v1/v2 → v3 migration (UserWaypoints → Waypoints). Cleared after the
    // upgrade runs and never written back to disk on save — JsonIgnore on
    // the setter would defeat the receive path, so we rely on the upgrade
    // clearing the dictionary instead.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? LegacyFields { get; set; }

    // Display sub-label for the right-rail list — number of waypoints in
    // the cycle (the user-clicked rooms, not the BFS-filled intermediates).
    [JsonIgnore]
    public int RoomCount => Waypoints.Count;

    // ----- helpers (used by LoopManager + tests) ---------------------

    [SetsRequiredMembers]
    public Loop() { Name = ""; }

    [SetsRequiredMembers]
    public Loop(string name, IEnumerable<LoopWaypoint> waypoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(waypoints);
        Name = name;
        Waypoints = new List<LoopWaypoint>(waypoints);
    }

    [SetsRequiredMembers]
    public Loop(string name, IEnumerable<RoomKey> waypointKeys)
        : this(name, ToWaypoints(waypointKeys)) { }

    private static IEnumerable<LoopWaypoint> ToWaypoints(IEnumerable<RoomKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (RoomKey k in keys) yield return new LoopWaypoint(k);
    }
}
