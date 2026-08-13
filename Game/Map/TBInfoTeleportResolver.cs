using System.Collections.Generic;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Resolves a teleport-style exit (room Room.Cmd > 0 + exit modifier (Item: N))
// into the verbatim command the walker types to traverse it.
//
// TBInfo Action chains are newline-separated lines; each line is colon-separated
// directives whose first token is the keyword the player types. The teleport
// target is encoded as a teleport <room> <map> directive somewhere in the
// chain. Example:
//
//     go hole:message 767:teleport 487 2:message 768
//     enter hole:message 767:teleport 487 2:message 768
//     crawl hole:message 767:teleport 487 2:message 768
//
// All three lines lead to room 487 on map 2; the resolver returns the first
// matching keyword. Lines without a teleport directive (NPC services, gambling
// outcomes, etc.) are skipped.
public static class TBInfoTeleportResolver
{
    // A CMD text chain is a handful of linked blocks (a colour-code intro block
    // that LinkTo's the effect block, and so on); this bounds a malformed
    // self-referential LinkTo when walking it for destinations.
    private const int MaxChainDepth = 32;

    // Find the first keyword in store's entry for roomCmd whose teleport
    // directive matches destination. Returns null when the entry isn't in the
    // store, the entry has no Action chain, or no line teleports to the
    // requested key.
    public static string? Resolve(TBInfoStore store, int roomCmd, RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (roomCmd <= 0) return null;

        foreach ((string keyword, RoomKey dest, int _) in EnumerateTeleports(store, roomCmd))
        {
            if (dest.Equals(destination)) return keyword;
        }
        return null;
    }

    // Walk every teleport <room> <map> directive in the CMD's Action chain and
    // yield (keyword, destination, minLevel) for each one. Used by the room
    // tooltip to surface "use chime → 1/65" style commands (and any "Level 20+"
    // gate) so the user can see how to traverse a teleport-bypassed door without
    // opening the game data browser.
    //
    // A "minlevel N [failTB]" directive anywhere in the same line gates the
    // teleport: the player must be level ≥ N or the game jumps to the fail
    // textblock instead of teleporting. We surface N (the fail textblock id is
    // irrelevant to the walker).
    public static IEnumerable<(string Keyword, RoomKey Destination, int MinLevel)>
        EnumerateTeleports(TBInfoStore store, int roomCmd)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (roomCmd <= 0) yield break;

        TBInfoEntry? entry = store.GetEntry(roomCmd);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Action)) yield break;

        foreach (string raw in entry.Action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            string keyword = parts[0];
            if (string.IsNullOrWhiteSpace(keyword)) continue;

            int minLevel = 0;
            RoomKey dest = default;
            bool haveDest = false;

            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("minlevel ", StringComparison.OrdinalIgnoreCase))
                {
                    // `minlevel <N> [failTB]` — first arg is the level floor.
                    string[] lvlArgs = parts[i][9..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (lvlArgs.Length >= 1) int.TryParse(lvlArgs[0], out minLevel);
                    continue;
                }

                // Keep scanning past the first teleport for a trailing minlevel:
                // the gate can appear before OR after the teleport directive.
                if (haveDest) continue;
                if (TryParseTeleport(parts[i], out RoomKey d)) { dest = d; haveDest = true; }
            }

            if (haveDest) yield return (keyword, dest, minLevel);
        }
    }

    // Every teleport destination reachable through a room's CMD text chain,
    // following LinkTo continuations. EnumerateTeleports (above) reads only the
    // CMD's own block because a keyword-typed traversal needs the player keyword
    // that sits in the keyword slot of that block — a LinkTo-continuation block
    // carries the teleport as a bare engine directive with no player command, so
    // it can't be synthesised into a walker edge. But the teleport GLYPH and the
    // "Use Teleport" map-shift only need the landing room, so they walk the whole
    // chain: Paradigm's sea-captain docks reach their `teleport <room> <map>`
    // this way — the dock's CMD is a colour-code intro block that LinkTo's the
    // effect block where the teleport actually lives. Cycle-guarded and depth-
    // capped against a malformed self-referential LinkTo.
    public static IEnumerable<RoomKey> EnumerateTeleportDestinations(TBInfoStore store, int roomCmd)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (roomCmd <= 0) yield break;

        HashSet<int> visited = new();
        for (int cur = roomCmd, depth = 0; cur > 0 && depth < MaxChainDepth; depth++)
        {
            if (!visited.Add(cur)) yield break;
            TBInfoEntry? entry = store.GetEntry(cur);
            if (entry is null) yield break;

            if (!string.IsNullOrWhiteSpace(entry.Action))
                foreach (string raw in entry.Action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    // Scan every token — a continuation block's teleport sits on a
                    // keyword-less effect line, so the directive can be the first
                    // token, not only a trailing one as in a typed-command line.
                    foreach (string tok in line.Split(':', StringSplitOptions.TrimEntries))
                        if (TryParseTeleport(tok, out RoomKey dest)) yield return dest;
                }

            cur = entry.LinkTo;
        }
    }

    // `teleport <roomNum> <mapNum>` — note the Action field uses (room, map)
    // order (verified in real Rooms.json dumps; map is the second token). Shared
    // with GreetTeleportResolver so the room/map order lives in one place.
    internal static bool TryParseTeleport(string token, out RoomKey dest)
    {
        dest = default;
        if (!token.StartsWith("teleport ", StringComparison.OrdinalIgnoreCase)) return false;
        string[] args = token[9..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length < 2) return false;
        if (!int.TryParse(args[0], out int room)) return false;
        if (!int.TryParse(args[1], out int map))  return false;
        dest = new RoomKey(map, room);
        return true;
    }
}
