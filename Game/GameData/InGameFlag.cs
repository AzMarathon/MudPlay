using System.Text.Json;

namespace MudPlay.Game.GameData;

// The game data's "In Game" flag on Items and Monsters rows: 1 for what the realm actually
// puts in play, 0 for the sysop-only, unimplemented, or duplicate test rows that a player can
// never meet without a sysop's hand (the "bow of silver" item, the extra "dark cleric"
// NPCs). Only an explicit numeric 0 counts as out of play — a set that predates the flag
// (property absent or non-numeric) is treated as in play rather than blanking a table.
public static class InGameFlag
{
    public static bool IsOutOfPlay(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return false;
        if (!row.TryGetProperty("In Game", out JsonElement el)) return false;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) && v == 0;
    }
}
