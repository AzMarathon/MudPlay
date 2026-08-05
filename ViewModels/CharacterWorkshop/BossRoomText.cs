using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game.Map;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// Shared "map/room; map/room" text <-> RoomKey-list conversion for the boss row
// view-models (the table row and the Manage dialog row both parse the same field).
internal static class BossRoomText
{
    public static string Format(IEnumerable<string> rooms) => string.Join("; ", rooms);

    // Accepts ';' or newline between rooms and '/' or ',' within a room; drops
    // anything that isn't a valid map/room pair.
    public static List<string> Parse(string? text)
    {
        var outp = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return outp;
        foreach (string tok in text.Split(new[] { ';', '\n' }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            string t = tok.Trim().Replace(',', '/');
            if (RoomKey.TryParseWire(t, out RoomKey k)) outp.Add($"{k.Map}/{k.Room}");
        }
        return outp;
    }
}
