namespace MudPlay.Services;

// The four layers of the settings hierarchy, ordered lowest priority (Defaults)
// to highest (Character). Higher tiers override lower ones at read time, per
// MegaMUD-parity vocabulary.
public enum SettingsTier
{
    // "installed defaults" — app-shipped fallback values + imported game-data tables.
    Defaults = 0,

    // "for all characters" — Global/global.json.
    Global = 1,

    // "only for this realm" — the active realm of the BBS: BBS/{bbs}/bbs.json for
    // tab settings, the realm's folder for game-data overrides.
    Bbs = 2,

    // "only for this character" — BBS/{bbs}/profiles/{char}/profile.json.
    Character = 3,
}

// Short labels for the Game Data Browser "Use" column — MegaMUD parity
// (Def / Glob / Realm / Char).
public static class SettingsTierExtensions
{
    public static string ToShortLabel(this SettingsTier tier) => tier switch
    {
        SettingsTier.Defaults  => "Def",
        SettingsTier.Global    => "Glob",
        SettingsTier.Bbs       => "Realm",
        SettingsTier.Character => "Char",
        _ => tier.ToString(),
    };

    // Long MegaMUD-parity labels for the Game Data edit dialogs' Use-tier picker.
    // "Installed defaults" is the reset target — picking it wipes the record's
    // higher-tier overrides and restores the seeded value.
    public static string ToPickerLabel(this SettingsTier tier) => tier switch
    {
        SettingsTier.Defaults  => "Installed defaults",
        SettingsTier.Global    => "For all characters (global)",
        SettingsTier.Bbs       => "Only for this realm",
        SettingsTier.Character => "Only for this character",
        _ => tier.ToString(),
    };
}
