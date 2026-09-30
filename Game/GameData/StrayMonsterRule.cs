using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Game.Map;

namespace MudPlay.Game.GameData;

// A monster the game data flags in play ("In Game" = 1) that can still never spawn:
// it's placed nowhere, is in no lair, nothing summons it, and every room its
// "Summoned By" lists as a plain Group: spawn has a different NPC and no lair.
// What really blocks a room's group spawns is its set-monster field, which the data
// doesn't carry; the NPC column is only the placed NPC, and most rooms with one still
// spawn from their group (GAME_MECHANICS "`Summoned By` spawn tokens"). So this is a
// data heuristic that leans on every other source being missing too. In every imported
// set it singles out Cygani (#543), listed under Aiken's Magic Shoppe, whose set
// monster the Stock room file confirms is Aiken.
//
// Anything the rule can't read keeps the monster in play: a token of another shape, a
// room missing from the graph (not loaded yet, or outside it), or no tokens at all.
public static partial class StrayMonsterRule
{
    // Out of play for either reason: flagged by the game data, or a stray as above.
    // getRoom null skips the stray check (no room graph to check it against).
    public static bool IsOutOfPlay(JsonElement row, Func<RoomKey, Room?>? getRoom)
        => InGameFlag.IsOutOfPlay(row) || (getRoom is not null && IsStray(row, getRoom));

    public static bool IsStray(JsonElement row, Func<RoomKey, Room?> getRoom)
    {
        if (row.ValueKind != JsonValueKind.Object) return false;
        if (!row.TryGetProperty("Number", out JsonElement numEl) || !numEl.TryGetInt32(out int number))
            return false;
        if (!row.TryGetProperty("Summoned By", out JsonElement sbEl) || sbEl.ValueKind != JsonValueKind.String)
            return false;

        bool anyGroup = false;
        foreach (string raw in (sbEl.GetString() ?? string.Empty).Split(','))
        {
            string token = raw.Trim();
            if (token.Length == 0) continue;
            Match m = GroupToken().Match(token);
            if (!m.Success) return false;   // placed, lair, summoned, or unreadable

            anyGroup = true;
            Room? room = getRoom(new RoomKey(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
            if (room is null || room.HasLair || room.Npc == 0 || room.Npc == number) return false;
        }
        return anyGroup;
    }

    [GeneratedRegex(@"^Group:\s*(\d+)/(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupToken();
}
