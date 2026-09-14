using System;
using System.Collections.Generic;
using System.Linq;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One toggleable column in the Level Projection grid: the key persisted in the
// character profile, the header, the LevelProjectionRow property its cells bind
// to, a pixel width, and whether it shows for a character that has never opened
// the picker. Muted renders the column in the secondary foreground — the grid
// alternates emphasis so the headline numbers (level, XP, HP, mana, accuracy,
// damage) stay readable against the supporting ones.
//
// The catalogue order IS the grid order; the picker only toggles visibility.
public readonly record struct LevelProjectionColumn(
    string Key, string Header, string Binding, int Width, bool DefaultVisible, bool Muted)
{
    // Level is the row's identity — hiding it would leave rows unlabelled, so the
    // picker renders its checkbox disabled the way the Game Data browser pins Name.
    public const string PinnedKey = "level";

    // Every column the grid can show, in display order. Keys are persisted, so
    // renaming one silently resets that column for existing characters — add a new
    // key instead. The utility skills default off: they only matter to classes that
    // were granted them, and 21 columns at once is unreadable.
    public static IReadOnlyList<LevelProjectionColumn> All { get; } = new[]
    {
        new LevelProjectionColumn(PinnedKey, "Lvl", "Level", 60, true, false),
        new LevelProjectionColumn("exptolevel", "Exp to level", "ExpToLevel", 150, true, false),
        new LevelProjectionColumn("totalxp", "Total XP", "TotalXp", 150, true, true),
        new LevelProjectionColumn("cost", "Train (copper)", "Cost", 130, true, true),
        new LevelProjectionColumn("hp", "HP", "HpRange", 120, true, false),
        new LevelProjectionColumn("hpregen", "HP/tick (idle/rest)", "HpRegen", 130, true, true),
        new LevelProjectionColumn("mana", "Mana / Kai", "Mana", 100, true, false),
        new LevelProjectionColumn("mpregen", "MP / tick", "MpRegen", 90, true, true),
        new LevelProjectionColumn("spellcasting", "Spellcast", "Spellcasting", 90, false, true),
        new LevelProjectionColumn("accuracy", "Accy", "Accuracy", 70, true, false),
        new LevelProjectionColumn("bsaccuracy", "BS Accy", "BsAccuracy", 80, false, false),
        new LevelProjectionColumn("crit", "Crit", "Crit", 70, true, true),
        new LevelProjectionColumn("dodge", "Dodge", "Dodge", 70, true, true),
        new LevelProjectionColumn("stealth", "Stealth", "Stealth", 80, true, true),
        new LevelProjectionColumn("meleedmg", "Melee dmg", "MeleeDmg", 100, true, false),
        new LevelProjectionColumn("maxenc", "Max enc", "MaxEnc", 90, true, true),
        new LevelProjectionColumn("magicres", "Magic res", "MagicRes", 90, true, true),
        new LevelProjectionColumn("perception", "Percep", "Perception", 80, false, true),
        new LevelProjectionColumn("thievery", "Thievery", "Thievery", 80, false, true),
        new LevelProjectionColumn("traps", "Traps", "Traps", 70, false, true),
        new LevelProjectionColumn("picklocks", "Picklocks", "Picklocks", 85, false, true),
        new LevelProjectionColumn("tracking", "Tracking", "Tracking", 80, false, true),
    };

    // The columns to render for a saved HIDDEN set. Storing the exceptions rather
    // than the selection is what lets a column added in a later release show up on
    // an existing character instead of staying invisible because its key predates
    // the save. A null save (picker never touched) falls back to DefaultVisible.
    // The pinned column survives a save that somehow lists it.
    public static IReadOnlyList<LevelProjectionColumn> Resolve(IReadOnlyList<string>? hidden)
    {
        if (hidden is null) return All.Where(c => c.DefaultVisible).ToList();

        HashSet<string> off = new(hidden, StringComparer.OrdinalIgnoreCase);
        return All.Where(c => c.Key == PinnedKey || !off.Contains(c.Key)).ToList();
    }
}
