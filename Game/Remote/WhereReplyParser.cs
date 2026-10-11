using System.Text.RegularExpressions;
using MudPlay.Game.Map;

namespace MudPlay.Game.Remote;

// Pulls the map/room out of a MudPlay @where reply telepath. The responder builds
// it via PartyEssentialHandlers.FormatRoom, wrapped with an exits tail:
// "{Adventurer's Guild, Universal Trainer (map 1, room 1376); exits: west}".
//
// The match REQUIRES that MudPlay wrapper — a leading '{' and the parenthesised
// "(map N, room M)" coordinate — so a human telepath that merely mentions "map 9,
// room 1012" in prose can't be mistaken for a location reply. Shared by
// PartyComebackManager's member-recovery @where probe and the nav-map @where
// highlight so the two never drift on what counts as a reply.
public static partial class WhereReplyParser
{
    [GeneratedRegex(@"\{[^}]*\(map (\d+), room (\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPattern();

    // True when message is a MudPlay @where reply; room is its map/room. False (room
    // = default) for any other telepath.
    public static bool TryParseRoom(string? message, out RoomKey room)
    {
        room = default;
        if (string.IsNullOrEmpty(message)) return false;
        // A `@loop send` line names a loop and an @events one an event, and either
        // can be named after a room, "(map 1, room 5)" and all: it says nothing of
        // where its sender stands.
        if (UserNamedReply.IsMessage(message)) return false;
        Match m = ReplyPattern().Match(message);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, out int map) || map <= 0) return false;
        if (!int.TryParse(m.Groups[2].Value, out int r) || r <= 0) return false;
        room = new RoomKey(map, r);
        return true;
    }
}
