using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MudPlay.Services;

namespace MudPlay.Game.Map.MpFile;

// Translates a decoded MegaMUD loop onto our map, one row per .mp step, so the
// review window can show the recording and our version side by side.
//
// The .mp file carries no (map, room) — only each step's hashExits (a 3-char hash of
// the room name + the room's exits). Translation:
//   1. Anchor: every room whose hashExits equals the file's start hash (the hash is
//      lossy, so there can be several); each is walked, and the one with the fewest
//      untranslated steps wins.
//   2. Walk: from the room at step i, a compass step follows our exit; a command step
//      ("go path", "pull lever", "sea") lands on the neighbour — or the room itself,
//      for a command that reveals an exit — whose hash matches step i+1 (exact first,
//      then a unique name-hash match, since exit sets drift between game versions).
//   3. A step we can't follow leaves the next row BLANK rather than failing the
//      import; the walk then looks for the following steps' rooms by hash near the
//      last room it knew, and resumes when exactly one matches.
//   4. Rooms the user sets by hand (fixedRooms) override the walk at their row and it
//      carries on from there.
// MegaMUD's Rooms.md, when given, pins rooms the hash alone can't: its room names
// usually end in a "-map room" hint, for the start room and for any named room the
// walk has lost track of.
public sealed partial class MpFileImporter
{
    // How far from the last known room to look for a step's room after a gap. Grows
    // by one per blank step, capped, so a longer gap still finds its way back.
    private const int ResyncRadius = 6;
    private const int MaxResyncRadius = 12;

    private readonly RoomGraphManager _graph;
    private readonly LogService? _log;

    public MpFileImporter(RoomGraphManager graph, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _log = log;
    }

    // Most name-hash-only starts we'll walk when nothing better matches.
    private const int MaxNameOnlyAnchors = 60;

    // Where the loop could start: rooms whose hashExits match its start room, plus the
    // room a "-map room" hint in the label or header names (MegaMUD's naming habit).
    // When neither finds anything — the start room's exits changed since the file was
    // recorded — rooms matching on the name hash alone.
    public IReadOnlyList<RoomKey> FindAnchorCandidates(MpLoopFile file, MegaMudRoomsFile? roomsMd = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        HashSet<RoomKey> found = new();
        foreach (Room room in _graph.Rooms)
            if (string.Equals(MegaMudHash.ComputeHashExits(room), file.StartHashExits, StringComparison.OrdinalIgnoreCase))
                found.Add(room.Key);
        if (StartHint(file, roomsMd) is { } hint) found.Add(hint);

        if (found.Count == 0 && MegaMudHash.Split(file.StartHashExits).NameHash is { } nameHash)
            foreach (Room room in _graph.Rooms)
            {
                if (!string.Equals(MegaMudHash.ComputeNameHash(room.Name), nameHash, StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(room.Key);
                if (found.Count >= MaxNameOnlyAnchors) break;
            }

        List<RoomKey> sorted = found.ToList();
        sorted.Sort((a, b) => a.Map != b.Map ? a.Map.CompareTo(b.Map) : a.Room.CompareTo(b.Room));
        return sorted;
    }

    // The start room as the file names it: a "-map room" hint on the label or header
    // room, or on the name Rooms.md gives the start room's code or hash. Null when
    // nothing names it — the start can only be guessed from its hash.
    public RoomKey? StartHint(MpLoopFile file, MegaMudRoomsFile? roomsMd)
    {
        IEnumerable<string> names = new[] { file.Label, file.Start.Name };
        if (roomsMd is not null)
        {
            if (roomsMd.ByCode(file.Start.Code) is { } byCode) names = names.Append(byCode.Name);
            names = names.Concat(roomsMd.ByHash(file.StartHashExits).Select(e => e.Name));
        }
        foreach (string name in names)
            if (MapRoomHint(name) is { } k && _graph.GetRoom(k) is not null)
                return k;
        return null;
    }

    // The room Rooms.md names for a step's hash, when exactly one entry has that hash
    // and its name carries a "-map room" hint that's in our map.
    private RoomKey? RoomsMdHint(MegaMudRoomsFile? roomsMd, string hash)
    {
        if (roomsMd is null) return null;
        List<MegaMudRoomEntry> hits = roomsMd.ByHash(hash).ToList();
        return hits.Count == 1 && MapRoomHint(hits[0].Name) is { } k && _graph.GetRoom(k) is not null ? k : null;
    }

    // "Wererat loop-8 910" → 8/910.
    internal static RoomKey? MapRoomHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Match m = MapRoomHintRegex().Match(text.Trim());
        return m.Success && int.TryParse(m.Groups["map"].Value, out int map) && int.TryParse(m.Groups["room"].Value, out int room)
               && map > 0 && room > 0
            ? new RoomKey(map, room)
            : null;
    }

    [GeneratedRegex(@"-\s*(?<map>\d+)\s+(?<room>\d+)\s*$")]
    private static partial Regex MapRoomHintRegex();

    // Walk every anchor candidate and return them ranked best first (fewest blanks,
    // then closing, then fewest hash drifts). Empty when no room matches the start.
    public IReadOnlyList<MpTranslation> TranslateCandidates(MpLoopFile file, MegaMudRoomsFile? roomsMd = null)
    {
        List<MpTranslation> all = FindAnchorCandidates(file, roomsMd).Select(a => Translate(file, a, null, roomsMd)).ToList();
        all.Sort(CompareQuality);
        _log?.Info("MpImporter",
            $"'{file.Label}': {all.Count} anchor candidate(s)"
            + (all.Count > 0 ? $"; best {all[0].Anchor} — {all[0].Summary}" : ""));
        return all;
    }

    private static int CompareQuality(MpTranslation a, MpTranslation b)
    {
        int c = a.Blanks.CompareTo(b.Blanks);
        if (c != 0) return c;
        c = b.Closes.CompareTo(a.Closes);
        if (c != 0) return c;
        c = a.Drift.CompareTo(b.Drift);
        if (c != 0) return c;
        return a.Anchor.Map != b.Anchor.Map ? a.Anchor.Map.CompareTo(b.Anchor.Map) : a.Anchor.Room.CompareTo(b.Anchor.Room);
    }

    public MpTranslation Translate(MpLoopFile file, RoomKey anchor, IReadOnlyDictionary<int, RoomKey>? fixedRooms = null,
        MegaMudRoomsFile? roomsMd = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        int n = file.Steps.Count;
        RoomKey?[] rooms = new RoomKey?[n];
        MpRowStatus[] status = new MpRowStatus[n];
        string?[] notes = new string?[n];
        bool[] passageKnown = new bool[n];

        if (n == 0) return new MpTranslation(anchor, Array.Empty<MpTranslatedRow>(), false, "the file has no steps");

        rooms[0] = fixedRooms is not null && fixedRooms.TryGetValue(0, out RoomKey f0) ? f0 : anchor;
        status[0] = fixedRooms?.ContainsKey(0) == true ? MpRowStatus.UserSet : StatusFor(rooms[0]!.Value, file.Steps[0]);
        RoomKey lastKnown = rooms[0]!.Value;
        int gap = 0;
        bool closes = false;
        string closure = string.Empty;

        for (int i = 0; i < n; i++)
        {
            bool last = i == n - 1;
            string destHash = last ? file.EndHashExits : file.Steps[i + 1].HashExits;
            (RoomKey? dest, string? why, bool throughPassage) = rooms[i] is { } from
                ? Follow(from, file.Steps[i], destHash)
                : (null, "the step before didn't translate", false);
            passageKnown[i] = throughPassage;

            if (last)
            {
                if (dest is { } end)
                {
                    closes = rooms[0] is { } start && end.Equals(start);
                    closure = closes
                        ? "closes back at the start room"
                        : $"the last step lands at {Describe(end)}, not back at the start";
                }
                else closure = $"the last step can't be followed: {why}";
                break;
            }

            int next = i + 1;
            if (fixedRooms is not null && fixedRooms.TryGetValue(next, out RoomKey userRoom))
            {
                rooms[next] = userRoom;
                status[next] = MpRowStatus.UserSet;
                if (dest is { } walked && !walked.Equals(userRoom))
                    notes[next] = $"you set this; the recorded step leads to {Describe(walked)}";
            }
            else if (dest is { } d)
            {
                rooms[next] = d;
                status[next] = StatusFor(d, file.Steps[next]);
            }
            else if (RoomsMdHint(roomsMd, file.Steps[next].HashExits) is { } named)
            {
                rooms[next] = named;
                status[next] = MpRowStatus.Resynced;
                notes[next] = "named by Rooms.md";
            }
            else if (Resync(lastKnown, file.Steps[next].HashExits, Math.Min(ResyncRadius + gap, MaxResyncRadius)) is { } found)
            {
                rooms[next] = found;
                status[next] = MpRowStatus.Resynced;
                notes[next] = $"found by its hash near {Describe(lastKnown)}";
            }
            else
            {
                status[next] = MpRowStatus.Blank;
                notes[next] = why;
            }

            if (rooms[next] is { } known) { lastKnown = known; gap = 0; }
            else gap++;
        }

        List<MpTranslatedRow> rows = new(n);
        for (int i = 0; i < n; i++)
            rows.Add(new MpTranslatedRow(i, rooms[i], status[i], notes[i], passageKnown[i]));
        return new MpTranslation(anchor, rows, closes, closure);
    }

    // MegaMUD's recorded moves taken literally from start on our map, for the review
    // window's map comparison: each compass step follows our exit, each command step
    // lands where Follow puts it. There's no re-matching — a step we can't follow
    // leaves us where we stand (that room goes in Stuck) and the next step carries on
    // from there, so the line shows exactly where the recording and our map part ways.
    public (IReadOnlyList<RoomKey> Path, IReadOnlyList<RoomKey> Stuck) DeadReckon(MpLoopFile file, RoomKey start)
    {
        List<RoomKey> path = new() { start };
        List<RoomKey> stuck = new();
        RoomKey cursor = start;
        for (int i = 0; i < file.Steps.Count; i++)
        {
            string destHash = i == file.Steps.Count - 1 ? file.EndHashExits : file.Steps[i + 1].HashExits;
            (RoomKey? dest, _, _) = Follow(cursor, file.Steps[i], destHash);
            if (dest is not { } d)
            {
                stuck.Add(cursor);
                continue;
            }
            if (d.Equals(cursor)) continue;
            path.Add(d);
            cursor = d;
        }
        return (path, stuck);
    }

    // Where step (taken from room) leads, or why it can't be followed. ThroughPassage
    // is true when a command step moved us through one of our own exits — the walker
    // takes that passage by itself, so the command needn't be carried.
    private (RoomKey? Dest, string? Why, bool ThroughPassage) Follow(RoomKey from, MpStep step, string destHash)
    {
        if (_graph.GetRoom(from) is not { } room)
            return (null, $"{from} isn't in the map data", false);

        if (step.Compass is { } dir)
        {
            if (room.Exits.TryGetValue(dir, out RoomExit exit)) return (exit.Target, null, false);
            string have = room.Exits.Count == 0
                ? "none"
                : string.Join(", ", room.Exits.Select(kv => $"{kv.Key}→{kv.Value.Target}"));
            return (null, $"{Describe(from)} has no {dir} exit in our data (it has: {have})", false);
        }

        // A command: the neighbour (or this room, for a command that reveals an exit
        // and so changes the room's hash) whose hash is the next step's.
        IEnumerable<RoomKey> candidates = room.Exits.Values.Select(e => e.Target).Append(from).Distinct();
        (string? destName, _) = MegaMudHash.Split(destHash);
        RoomKey? byName = null;
        int nameHits = 0;
        foreach (RoomKey c in candidates)
        {
            if (_graph.GetRoom(c) is not { } cand) continue;
            if (string.Equals(MegaMudHash.ComputeHashExits(cand), destHash, StringComparison.OrdinalIgnoreCase))
                return (c, null, !c.Equals(from));
            if (destName is not null && string.Equals(MegaMudHash.ComputeNameHash(cand.Name), destName, StringComparison.OrdinalIgnoreCase))
            {
                byName = c;
                nameHits++;
            }
        }
        if (nameHits == 1) return (byName, null, !byName!.Value.Equals(from));
        return (null, nameHits > 1
            ? $"'{step.RawAction}' from {Describe(from)}: {nameHits} neighbours share the next room's name hash"
            : $"'{step.RawAction}' from {Describe(from)}: no neighbour matches the next room (hash {destHash}) — the passage isn't in our data", false);
    }

    // The one room within radius moves of near whose hashExits is hash, or null when
    // none or several match.
    private RoomKey? Resync(RoomKey near, string hash, int radius)
    {
        HashSet<RoomKey> seen = new() { near };
        List<RoomKey> frontier = new() { near };
        RoomKey? match = null;
        for (int depth = 0; depth <= radius && frontier.Count > 0; depth++)
        {
            List<RoomKey> nextFrontier = new();
            foreach (RoomKey k in frontier)
            {
                if (_graph.GetRoom(k) is not { } r) continue;
                if (string.Equals(MegaMudHash.ComputeHashExits(r), hash, StringComparison.OrdinalIgnoreCase))
                {
                    if (match is not null && !match.Value.Equals(k)) return null;
                    match = k;
                }
                foreach (RoomExit e in r.Exits.Values)
                    if (seen.Add(e.Target)) nextFrontier.Add(e.Target);
            }
            frontier = nextFrontier;
        }
        return match;
    }

    private MpRowStatus StatusFor(RoomKey key, MpStep step) =>
        _graph.GetRoom(key) is { } r
        && string.Equals(MegaMudHash.ComputeHashExits(r), step.HashExits, StringComparison.OrdinalIgnoreCase)
            ? MpRowStatus.Exact
            : MpRowStatus.HashDrift;

    private string Describe(RoomKey k) =>
        _graph.GetRoom(k) is { } r ? $"{k} '{r.Name}'" : k.ToString();

    // The waypoint a translated row starts as, before the user edits it. MegaMUD's
    // "Don't rest" / "Don't attack" carry over, and its "Rest up here" (rest to full
    // before this step) becomes both rest-here flags. A command step keeps its
    // command only when our map doesn't already know the passage — when it does, the
    // walker takes it by itself. Searches never carry: hidden exits are searched for
    // automatically.
    public static LoopWaypoint DefaultWaypoint(MpStep step, RoomKey room, bool passageKnown)
    {
        string? command = step.Command is { } c && !passageKnown && !IsSearch(c) ? c : null;
        bool rest = step.Flags.HasFlag(MpStepFlags.RestHere);
        return new LoopWaypoint(room, command,
            doNotRest: step.Flags.HasFlag(MpStepFlags.DontRest),
            doNotAttack: step.Flags.HasFlag(MpStepFlags.NoAttack))
        {
            RestHereHp = rest,
            RestHereMana = rest,
        };
    }

    internal static bool IsSearch(string command)
    {
        string verb = command.Trim().Split(' ')[0];
        return verb.Equals("sea", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("search", StringComparison.OrdinalIgnoreCase);
    }

    // The loop's waypoints from edited rows: blanks dropped, and a row that repeats
    // the room before it (a command that didn't move) folded into that waypoint, its
    // command and flags merged in.
    public static List<LoopWaypoint> Waypoints(IEnumerable<LoopWaypoint?> rows)
    {
        List<LoopWaypoint> result = new();
        foreach (LoopWaypoint? w in rows)
        {
            if (w is null || w.Key.Equals(default(RoomKey))) continue;
            if (result.Count > 0 && result[^1].Key.Equals(w.Key))
            {
                LoopWaypoint prev = result[^1];
                if (prev.Command is null && w.Command is not null)
                {
                    prev.Command = w.Command;
                    prev.DelayMs = w.DelayMs;
                }
                prev.DoNotRest |= w.DoNotRest;
                prev.DoNotAttack |= w.DoNotAttack;
                prev.RestHereHp |= w.RestHereHp;
                prev.RestHereMana |= w.RestHereMana;
                continue;
            }
            result.Add(new LoopWaypoint(w.Key, w.Command, w.DelayMs, w.DoNotRest, w.DoNotAttack)
            {
                RestHereHp = w.RestHereHp,
                RestHereMana = w.RestHereMana,
            });
        }
        // The loop closes back on its first room, so a trailing copy of it goes.
        if (result.Count > 1 && result[^1].Key.Equals(result[0].Key)) result.RemoveAt(result.Count - 1);
        return result;
    }

    // Drop a trailing "-map room" hint ("Frozen Caverns (wolves)-10 35").
    internal static string StripMapRoomSuffix(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string trimmed = raw.Trim();
        Match m = SuffixRegex().Match(trimmed);
        return m.Success ? trimmed[..m.Index].TrimEnd() : trimmed;
    }

    // "-N M" or "-N" at the end; the dash is required so "Crypt Level 1" keeps its 1.
    [GeneratedRegex(@"-\s*\d+(?:\s+\d+)?\s*$")]
    private static partial Regex SuffixRegex();
}

// How a translated row's room was found.
public enum MpRowStatus
{
    Exact,       // walked there, and its hash matches the recording
    HashDrift,   // walked there, but its name or exits differ from the recording
    Resynced,    // found by hash near the last known room after a gap
    UserSet,     // set by hand in the review window
    Blank,       // couldn't be translated
}

// Row Index is the .mp step index; Room the room we stand in for that step.
// PassageKnown marks a command step that moved us through one of our own exits.
public sealed record MpTranslatedRow(int Index, RoomKey? Room, MpRowStatus Status, string? Note, bool PassageKnown);

public sealed record MpTranslation(RoomKey Anchor, IReadOnlyList<MpTranslatedRow> Rows, bool Closes, string ClosureNote)
{
    public int Blanks => Rows.Count(r => r.Status == MpRowStatus.Blank);
    public int Drift => Rows.Count(r => r.Status == MpRowStatus.HashDrift);

    public string Summary =>
        (Blanks == 0 ? "every step translated" : $"{Blanks} step(s) untranslated")
        + (Drift > 0 ? $", {Drift} room(s) differ from the recording" : "")
        + $"; {ClosureNote}";
}
