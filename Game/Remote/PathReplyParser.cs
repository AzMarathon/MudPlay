using System.Text.RegularExpressions;
using MudPlay.Game.Map;

namespace MudPlay.Game.Remote;

// Pulls a PathReport out of a MudPlay @path reply. The responder
// (PartyEssentialHandlers.OnPath) sends "{<engine phrase>; <room> (map M, room R);
// step X/Y}", where the engine phrase is "walking to M/R" (or "<reason> en route to
// M/R" while paused), "running loop 'Name'" (or "<reason> on loop 'Name'"), or an
// auto-lair / sailing phrase. Only replies carrying the room AND the step tail parse:
// that tail is what tells an @path reply from an @where one, and a reply without it
// (no path loaded) has nothing to draw.
public static partial class PathReplyParser
{
    [GeneratedRegex(@"^\s*\{(?<body>.*)\}\s*$")]
    private static partial Regex Wrapped();

    [GeneratedRegex(@"\(map (?<map>\d+), room (?<room>\d+)\); step (?<step>\d+)/(?<total>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RoomAndStep();

    [GeneratedRegex(@"^(?:walking to|.+ en route to) (?<map>\d+)/(?<room>\d+);", RegexOptions.IgnoreCase)]
    private static partial Regex WalkDestination();

    [GeneratedRegex(@"^(?:running loop|.+ on loop) '(?<name>[^']+)';", RegexOptions.IgnoreCase)]
    private static partial Regex LoopName();

    public static bool TryParse(string? message, out PathReport? report)
    {
        report = null;
        if (string.IsNullOrEmpty(message)) return false;
        Match wrapped = Wrapped().Match(message);
        if (!wrapped.Success) return false;
        string body = wrapped.Groups["body"].Value.Trim();

        Match tail = RoomAndStep().Match(body);
        if (!tail.Success) return false;
        if (!TryKey(tail.Groups["map"].Value, tail.Groups["room"].Value, out RoomKey leaderRoom)) return false;
        if (!int.TryParse(tail.Groups["step"].Value, out int step) || step <= 0) return false;
        if (!int.TryParse(tail.Groups["total"].Value, out int total) || total <= 0) return false;

        RoomKey? destination = null;
        Match walk = WalkDestination().Match(body);
        if (walk.Success && TryKey(walk.Groups["map"].Value, walk.Groups["room"].Value, out RoomKey dest))
            destination = dest;

        Match loop = LoopName().Match(body);
        string? loopName = loop.Success ? loop.Groups["name"].Value : null;

        report = new PathReport(leaderRoom, destination, loopName, Math.Min(step, total), total);
        return true;
    }

    private static bool TryKey(string map, string room, out RoomKey key)
    {
        key = default;
        if (!int.TryParse(map, out int m) || m <= 0) return false;
        if (!int.TryParse(room, out int r) || r <= 0) return false;
        key = new RoomKey(m, r);
        return true;
    }
}
