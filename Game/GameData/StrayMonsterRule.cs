using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Game.Map;

namespace MudPlay.Game.GameData;

// A monster the game data flags in play ("In Game" = 1) that can still never spawn:
// it's placed nowhere, is in no lair, nothing summons it, and every room its
// "Summoned By" lists as a plain Group: spawn already spawns a different NPC. That
// NPC fills the room's spawn slot, and the engine only draws from the room's monster
// group while that slot is empty (GAME_MECHANICS "`Summoned By` spawn tokens"). In
// every imported set this singles out Cygani (#543), listed under Aiken's Magic Shoppe.
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
