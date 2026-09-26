using System.Collections.Generic;
using MudPlay.Game.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Unobtainable tab. Lists the Items and Monsters rows the game marks
// out of play ("In Game" == 0) — sysop-only, unimplemented, or duplicate test rows ("bow of
// silver", the "large rock" placeholders, "longsword1..5", the extra "dark cleric" NPCs).
// The Item Finder catalogue skips the items (ItemFinderEntry.IsObtainable) and the Monsters
// table leaves the monsters out, so nothing only a sysop can produce passes for a real
// spawn; rather than leaving them simply hidden, this read-only view collects them so they
// can be inspected. The Kind column says which table a row came from.
public sealed class UnobtainableSectionViewModel : JsonTableSectionViewModel
{
    private static readonly IReadOnlyDictionary<string, string?> ItemKind =
        new Dictionary<string, string?> { ["Kind"] = "Item" };
    private static readonly IReadOnlyDictionary<string, string?> MonsterKind =
        new Dictionary<string, string?> { ["Kind"] = "Monster" };

    public UnobtainableSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null)
        : base(cache, resolver) { }

    public override string Id => "unobtainable";
    public override string Title => "Unobtainable";

    // The primary table; PopulateRows adds the Monsters rows as well.
    protected override string TableName => "Items";

    // An unobtainable row carries the same fields as the record it came from, so the table
    // shows them all (read-only) rather than a curated slice: the item block mirrors
    // ItemsSectionViewModel.Columns, then the monster stats. ArmourClass / DamageResist are
    // fields of both, so they share a column.
    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number", "Name", "Kind",
        "ItemType", "Worn", "WeaponType", "ArmourType", "Min", "Max",
        "ArmourClass", "DamageResist", "Speed", "Accy", "StrReq", "Encum", "Price", "Currency",
        "HP", "EXP", "AvgDmg", "Align",
    };

    public override IReadOnlyDictionary<string, string>? ColumnHeaders { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EXP"]    = "Exp",
            ["AvgDmg"] = "Avg Damage",
            ["Align"]  = "Alignment",
        };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "unobtainable", "in game", "sysop", "unimplemented", "placeholder", "test item",
        "monster", "mob", "npc",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemType"]   = LookupEnums.FormatItemType,
            ["Worn"]       = LookupEnums.FormatWornSlot,
            ["WeaponType"] = LookupEnums.FormatWeaponType,
            ["ArmourType"] = LookupEnums.FormatArmourType,
            ["Currency"]   = LookupEnums.FormatCurrency,
            ["HP"]         = MonstersSectionViewModel.FormatThousands,
            ["EXP"]        = MonstersSectionViewModel.FormatThousands,
            ["Align"]      = LookupEnums.FormatMonAlignment,
        };

    // Items, then monsters, each limited to the rows whose "In Game" is explicitly 0 — the
    // inverse of ItemFinderEntry.IsObtainable and of the Monsters table's filter. Absent /
    // non-numeric / non-zero means in play, so it's excluded.
    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        AddTableRows(rows, "Items", InGameFlag.IsOutOfPlay, static _ => ItemKind);
        AddTableRows(rows, "Monsters", InGameFlag.IsOutOfPlay, static _ => MonsterKind);
    }
}
