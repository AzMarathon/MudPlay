namespace MudPlay.ViewModels.CharacterWorkshop;

// One aggregated bonus row — the Equipment Manager's Equipment Bonuses box, the
// Item Finder's trial set and Character Info's Quest Bonuses box: a stat label
// (Stat, e.g. "Armour Class"), its net bonus value (Value, e.g. "+15" or "5.0"),
// and a newline-joined tooltip listing which sources contributed what (Tooltip;
// null when there's only one trivial source worth no hover detail).
public sealed record EquipBonusRow(string Stat, string Value, string? Tooltip)
{
    // True when the net bonus is a penalty. Value is always signed ("+0;-0" /
    // "+0.#;-0.#") and zero rows are filtered out, so a leading minus is an
    // unambiguous negative — it drives the red vs. neutral coloring in the
    // Equipment Bonuses list.
    public bool IsNegative => Value.StartsWith('-');
}
