using System.Text.Json;
using System.Text.Json.Nodes;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// One place the Game Data edit dialogs (Monsters, Items) route a saved override
// through, so the tier semantics are identical everywhere the Use picker appears.
//
// Three outcomes, keyed off the tier the user chose and whether the edit ended up
// equal to the installed defaults:
//   * Installed defaults tier  → a confirm, then ResetGameDataRecord wipes the
//     record's Global / BBS / Character overrides so it falls back to the seed.
//   * edit == installed defaults → the chosen tier's override is redundant, so
//     ClearGameDataAt removes it (an edit dragged back to the seeded value shifts
//     the row back toward Def instead of writing a no-op override).
//   * otherwise → WriteGameDataAt stores the delta at the chosen tier.
//
// Items save field by field instead (ApplyItemAsync / ApplyItemAt).
//
// Returns true when something changed so the caller reloads its table.
public static class GameDataOverrideApplier
{
    private const string ItemsTable = "Items";

    public static async Task<bool> ApplyAsync<T>(
        SettingsResolver resolver,
        ConfirmService confirm,
        string table,
        string recordId,
        SettingsTier tier,
        T overlay,
        bool equalsInstalledDefaults) where T : class
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(overlay);

        if (tier == SettingsTier.Defaults)
            return await ResetAsync(resolver, confirm, table, recordId);

        // A tier whose scope isn't active can't be written (Character with no profile
        // loaded, BBS with no active BBS) — fall back to the most-specific writable
        // tier so Save never throws. Defaults never reaches here.
        if (!resolver.CanWriteAt(tier)) tier = resolver.WritableTiers()[0];

        if (equalsInstalledDefaults)
            resolver.ClearGameDataAt(tier, table, recordId);
        else
            resolver.WriteGameDataAt(tier, table, recordId, overlay);
        return true;
    }

    // An item record saved from the item editor or the batch editor: the
    // installed-defaults reset as above, otherwise ApplyItemAt over every field.
    public static async Task<bool> ApplyItemAsync(
        SettingsResolver resolver,
        ConfirmService confirm,
        string recordId,
        SettingsTier tier,
        ItemOverlay wanted,
        ItemOverlay installedDefaults)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        if (tier == SettingsTier.Defaults)
            return await ResetAsync(resolver, confirm, ItemsTable, recordId);
        ApplyItemAt(resolver, recordId, tier, wanted, installedDefaults);
        return true;
    }

    // Save an item so that `tier` yields `wanted` for each field in `fields` (every
    // field when null), and leave the tier's other fields as they are (user,
    // 2026-10-17). Per field:
    //   * the wanted value is the installed default, and the tiers below give the
    //     installed default too → the tier holds nothing for it;
    //   * otherwise → the tier holds the wanted value, so a lower tier's different
    //     value can't show through: an untick over a Global tick is saved as off,
    //     and a record set back to its installed defaults over a Global or realm
    //     value keeps those defaults at this tier.
    // A tier left holding nothing loses its override, so a record set back to its
    // installed defaults with nothing below shifts back to them. Other tiers are
    // never touched.
    //
    // Unset, false and blank count as the same value: the engines read a missing
    // flag as off and a blank count as none.
    public static void ApplyItemAt(
        SettingsResolver resolver,
        string recordId,
        SettingsTier tier,
        ItemOverlay wanted,
        ItemOverlay installedDefaults,
        IReadOnlyCollection<string>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(installedDefaults);
        if (tier == SettingsTier.Defaults)
            throw new ArgumentException("The installed defaults are reset, not written.", nameof(tier));
        if (!resolver.CanWriteAt(tier)) tier = resolver.WritableTiers()[0];

        JsonObject want = ToJson(wanted);
        JsonObject defaults = ToJson(installedDefaults);
        JsonObject below = resolver.ResolveGameDataBelow(ItemsTable, recordId, installedDefaults, tier);
        JsonObject held = resolver.ReadGameDataAt(tier, ItemsTable, recordId) ?? new JsonObject();

        foreach ((string field, JsonNode? value) in want)
        {
            if (fields is not null && !fields.Contains(field)) continue;
            JsonNode? installed = defaults[field];
            JsonNode? shown = below[field];
            if (SameValue(value, installed) && SameValue(shown, installed))
            {
                held.Remove(field);
                continue;
            }
            // A flag left unset is saved as off, which is what it reads as.
            bool flag = IsFlag(installed) || IsFlag(shown);
            held[field] = value is null && flag ? JsonValue.Create(false) : value?.DeepClone();
        }

        if (held.Count == 0)
            resolver.ClearGameDataAt(tier, ItemsTable, recordId);
        else
            resolver.WriteGameDataAt(tier, ItemsTable, recordId, held);
    }

    private static async Task<bool> ResetAsync(
        SettingsResolver resolver, ConfirmService confirm, string table, string recordId)
    {
        bool ok = await confirm.ConfirmAsync(
            "Reset to installed defaults",
            "This resets the record to its seeded installed defaults, removing your "
            + "Global, BBS, and Character edits for this one record. This can't be undone.\n\n"
            + "Continue?",
            "Reset");
        if (!ok) return false;
        resolver.ResetGameDataRecord(table, recordId);
        return true;
    }

    private static JsonObject ToJson(ItemOverlay overlay) =>
        JsonSerializer.SerializeToNode(overlay, JsonStore.Options)!.AsObject();

    private static bool SameValue(JsonNode? a, JsonNode? b) =>
        (IsUnset(a) && IsUnset(b)) || JsonNode.DeepEquals(a, b);

    private static bool IsFlag(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    private static bool IsUnset(JsonNode? node) => node switch
    {
        null => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetValue<string>()),
        _ => false,
    };
}
