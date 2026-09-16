using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// An item's charge metadata, read from the active set's Items table: how many uses it
// holds and whether those uses come back at cleanup. Mirrors the two game-data fields
// the spellbook's UseCount already keys off, plus the retain/recharge flag:
//   UseCount           -1 = infinite (never depletes); >0 = finite max N; 0 = not a
//                      charged item.
//   Retain After Uses  1 = the item is retained when its uses run out and restocks to
//                      max at cleanup (rechargeable — tokens, quest cloaks, the
//                      Paradigm nexus spear); 0/absent = consumed when spent (truly
//                      finite — the gnarled / teak / mahogany wands).
public readonly record struct ItemChargeMeta(int MaxUses, bool Recharges)
{
    // A finite, trackable limited-use item (has a real charge ceiling). Infinite
    // (MaxUses <= 0) items carry no charge readout.
    public bool IsLimitedUse => MaxUses > 0;

    // Charge metadata for an item number in the active set, or null when the row isn't
    // found. Realm-correct automatically — it reads whichever set is loaded.
    public static ItemChargeMeta? Read(GameDataCache cache, int itemNumber)
    {
        if (cache is null || itemNumber <= 0) return null;
        if (cache.GetRawTable("Items") is not { } items) return null;
        foreach (JsonElement row in items.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (ReadInt(row, "Number") != itemNumber) continue;
            return new ItemChargeMeta(ReadInt(row, "UseCount"), ReadInt(row, "Retain After Uses") == 1);
        }
        return null;
    }

    private static int ReadInt(JsonElement row, string prop)
        => row.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.Number
           && el.TryGetInt32(out int v) ? v : 0;
}
