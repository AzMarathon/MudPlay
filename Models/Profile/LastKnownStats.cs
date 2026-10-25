using MudPlay.Services;

namespace MudPlay.Models.Profile;

// Persisted snapshot of the most recent Game.PlayerStats capture — written by
// Game.StatParser each time it closes a successful scan window, rehydrated into
// Game.PlayerStats on ProfileService.ProfileLoaded. The point is that when the
// user closes the client and reconnects, the live state surfaces start with the
// last-observed values instead of zeros — the next stat / exp just verifies /
// updates rather than establishing-from-scratch.
//
// Mirrors every field on Game.PlayerStats 1:1. Whenever a field is added there,
// this DTO and the snapshot / hydrate paths in Game.StatParser need to grow with
// it. (A test pins the field-count parity so a future addition can't silently
// drift.)
//
// Stored on CharacterProfile.LastKnownStats, which the app's save-on-close hook
// (MainWindow.Closing) flushes to disk for any loaded named profile. A draft
// profile (no name) doesn't persist — same rule as the rest of the profile DTO.
public sealed class LastKnownStats
{
    // JSON schema version for forward-compat migrations.
    public int SchemaVersion { get; set; } = 1;

    // ----- Identity ------------------------------------------------------
    public string Name { get; set; } = string.Empty;
    public string Race { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;

    // ----- Progression ---------------------------------------------------
    [SettingRange(0, 1_000_000)]
    public int Level { get; set; }
    // long, not int — Paradigm exp passes int32 (2.1B) into the hundreds of
    // billions. See PlayerStats._exp.
    public long Exp { get; set; }
    public int Lives { get; set; }
    public int Cp { get; set; }
    // The level whose training CP is counted in Cp (PlayerStats.CpThroughLevel).
    // Null in snapshots saved before it was kept: Level stands in.
    public int? CpThroughLevel { get; set; }
    public long ExpToNext { get; set; }
    public long LevelExpSpan { get; set; }
    public int LevelPercent { get; set; }

    // ----- Vitals --------------------------------------------------------
    public int Hits { get; set; }
    public int MaxHits { get; set; }
    public int Mana { get; set; }
    public int MaxMana { get; set; }
    public int Kai { get; set; }
    public int MaxKai { get; set; }
    public int ArmourClass { get; set; }
    public int MaxArmourClass { get; set; }

    // ----- Primary stats -------------------------------------------------
    [SettingRange(-1_000_000, 1_000_000)]
    public int Strength { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int Intellect { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int Willpower { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int Agility { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int Health { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int Charm { get; set; }

    // ----- Secondary skills ----------------------------------------------
    [SettingRange(-1_000_000, 1_000_000)]
    public int Perception { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int Stealth { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int Thievery { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int Traps { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int Picklocks { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int Tracking { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int MartialArts { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int MagicRes { get; set; }
    [SettingRange(-1_000_000, 1_000_000)]
    public int Spellcasting { get; set; }

    // ----- What the screen said about its own numbers ---------------------
    // Which of the six trainable stats that `stat` screen marked as modified, as
    // Game.StatSet bits. Null when the screen's marks weren't read (and in every
    // snapshot saved before marks were kept): the numbers above then can't be told
    // from buffed ones.
    public int? ModifiedStats { get; set; }
    // The active effects that screen listed under the stats.
    public List<SavedEffectLine>? ActiveEffects { get; set; }
}
