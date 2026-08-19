using System.Text.Json;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Resolves an item whose USE teleports the caster (via a cast-spell → textblock →
// teleport chain) into the data a routable graph edge needs. The one stock
// instance is the "potion of levitation" (item 992): using it casts spell 607,
// whose TextBlock ability points at a TBInfo whose Action reads
// `roomitem 993 …: teleport 1009 9` — teleport to 9/1009, gated on room-fixture
// item 993 being present (which pins the usable spot to 3/1). No other resolver
// reads an item's cast-ability into a teleport, so this fills that gap; it's a
// rare-but-real mechanic, resolved data-driven rather than hardcoded.
//
// The chain, decoded here: item Abil 43 (CastsSp) → spell number → that spell's
// Abil 148 (TextBlock) → TBInfo number → its Action's literal `teleport <room>
// <map>`. The `roomitem` gate on the same line anchors the SOURCE room; the caller
// resolves it (the fixture item's own room), since a teleport keyed only to an item
// has no exit / CMD / greet to hang the edge on.
public static class ItemUseTeleportResolver
{
    private const int CastsSpAbility = 43;    // item casts a spell when used
    private const int TextBlockAbility = 148; // spell points at a TBInfo chain
    private const int AbilitySlots = 20;

    // One item-use teleport: the item you carry + use (HolderItemId / Name), the
    // room-fixture item the teleport TBInfo gates on (GateItemId — the anchor the
    // caller resolves to a source room), the fixed destination, and any minlevel
    // floor. GateItemId is 0 when the teleport carries no roomitem gate.
    public readonly record struct ItemUseTeleport(
        int HolderItemId, string HolderItemName, int GateItemId, RoomKey Destination, int MinLevel);

    public static IEnumerable<ItemUseTeleport> Enumerate(
        JsonDocument itemsDoc, KnownSpellCatalog catalog, TBInfoStore store)
    {
        ArgumentNullException.ThrowIfNull(itemsDoc);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(store);

        foreach (JsonElement row in itemsDoc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int itemNumber) || itemNumber <= 0) continue;

            for (int i = 0; i < AbilitySlots; i++)
            {
                if (!TryReadInt(row, $"Abil-{i}", out int code) || code != CastsSpAbility) continue;
                if (!TryReadInt(row, $"AbilVal-{i}", out int spellNumber) || spellNumber <= 0) continue;
                if (catalog.GetFormulaByNumber(spellNumber) is not { } formula) continue;

                foreach (SpellAbility ab in formula.Abilities)
                {
                    if (ab.Code != TextBlockAbility || ab.Value <= 0) continue;
                    if (store.GetEntry(ab.Value) is not { } entry
                        || string.IsNullOrWhiteSpace(entry.Action)) continue;
                    if (!TryReadTeleport(entry.Action, out RoomKey dest, out int gate, out int minLevel)) continue;

                    string name = (TryReadString(row, "Name") ?? string.Empty).Trim();
                    yield return new ItemUseTeleport(itemNumber, name, gate, dest, minLevel);
                }
            }
        }
    }

    // Scan a TBInfo Action for a line carrying a literal `teleport <room> <map>`;
    // capture that line's `roomitem <id>` gate and any `minlevel <n>` floor (both
    // gate the same line, so they're read together).
    private static bool TryReadTeleport(string action, out RoomKey dest, out int gateItemId, out int minLevel)
    {
        dest = default;
        gateItemId = 0;
        minLevel = 0;
        foreach (string raw in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            RoomKey? found = null;
            int gate = 0;
            int min = 0;
            foreach (string part in raw.Split(':', StringSplitOptions.TrimEntries))
            {
                if (TBInfoTeleportResolver.TryParseTeleport(part, out RoomKey d)) found = d;
                else if (part.StartsWith("roomitem", StringComparison.OrdinalIgnoreCase)) gate = FirstInt(part);
                else if (part.StartsWith("minlevel", StringComparison.OrdinalIgnoreCase)) min = FirstInt(part);
            }
            if (found is { } dv) { dest = dv; gateItemId = gate; minLevel = min; return true; }
        }
        return false;
    }

    private static int FirstInt(string token)
    {
        foreach (string w in token.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(w, out int v)) return v;
        return 0;
    }

    private static bool TryReadInt(JsonElement row, string prop, out int value)
    {
        value = 0;
        return row.TryGetProperty(prop, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }

    private static string? TryReadString(JsonElement row, string prop)
        => row.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;
}
