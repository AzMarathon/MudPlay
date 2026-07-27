using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Game.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Unobtainable tab. Lists the Items rows the game marks out of play
// ("In Game" == 0) — sysop-only, unimplemented, or duplicate test items ("bow of silver",
// the "large rock" placeholders, "longsword1..5"). These are the rows the Item Finder
// catalogue deliberately skips (ItemFinderEntry.IsObtainable); rather than leaving them
// simply hidden, this read-only view collects them so they can be inspected. Reads the same
// Items table, filtered to the unobtainable rows.
public sealed class UnobtainableSectionViewModel : JsonTableSectionViewModel
{
    public UnobtainableSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null)
        : base(cache, resolver) { }

    public override string Id => "unobtainable";
    public override string Title => "Unobtainable";

    protected override string TableName => "Items";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number", "Name", "ItemType", "Worn", "WeaponType", "ArmourType",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "unobtainable", "in game", "sysop", "unimplemented", "placeholder", "test item",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemType"]   = LookupEnums.FormatItemType,
            ["Worn"]       = LookupEnums.FormatWornSlot,
            ["WeaponType"] = LookupEnums.FormatWeaponType,
            ["ArmourType"] = LookupEnums.FormatArmourType,
        };

    // The inverse of ItemFinderEntry.IsObtainable: keep only rows whose "In Game" is
    // explicitly 0. Absent / non-numeric / non-zero means obtainable, so it's excluded.
    protected override bool IncludeRow(JsonElement element)
    {
        if (!element.TryGetProperty("In Game", out JsonElement el)) return false;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int v)) return false;
        return v == 0;
    }
}
