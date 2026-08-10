using System.Text.Json;

namespace MudPlay.Models.Settings;

// Root DTO for Data/Global/global.json — the Global tier of the settings
// hierarchy. Holds app-wide deltas (the things every character shares) plus
// pointers the launcher needs before any profile is loaded. Anything in
// this file must be resolvable without a character profile loaded (Global
// tier is the highest non-Char layer in SettingsResolver).
public sealed class GlobalSettings
{
    // JSON schema version. Bump whenever the on-disk format changes in a
    // non-backward-compatible way; migration logic keys off this.
    public int SchemaVersion { get; set; } = 1;

    // (BBS, character) reference of the most recently loaded profile. Used
    // at startup to auto-load the last session; null on first run. Profiles
    // are BBS-scoped now, so the bare character name is no longer a unique
    // key.
    public Profile.ProfileRef? LastUsedProfile { get; set; }

    // When true, startup loads LastUsedProfile instead of opening a blank draft
    // (Settings → General). Off by default — the app opens blank and the user
    // picks / builds a profile, the historical behaviour.
    public bool AutoLoadLastProfile { get; set; }

    // The profile to auto-load at startup, or null to open the blank draft: the
    // last-used profile when AutoLoadLastProfile is on, else null. Stays null on
    // first run (no LastUsedProfile yet) even with the toggle on, so a fresh
    // install still opens blank.
    public Profile.ProfileRef? StartupProfile()
        => AutoLoadLastProfile ? LastUsedProfile : null;

    // Up to RecentProfilesLimit profiles the user has loaded recently,
    // ordered most-recent-first. Each entry is a (BBS, character)
    // reference. Drives the File → Recent profiles submenu.
    public List<Profile.ProfileRef>? RecentProfiles { get; set; }

    // Cap on RecentProfiles retention.
    public const int RecentProfilesLimit = 5;

    // Fallback game-data set name used when no BBS is pinned (or the pinned
    // BBS has no ActiveGameDataSet of its own). Once a BBS is pinned its
    // setting takes over via the resolution chain in AppServices.
    public string? DefaultGameDataSet { get; set; }

    // Auto-cleanup window for the Players table (see
    // PlayerDatabase.PurgeStale). Records whose LastSeenUtc falls outside
    // this window get dropped at startup so the table doesn't accumulate
    // one-shot tourists from every session. Per-record DontAutoDelete opts
    // out. 0 or negative disables auto-cleanup entirely.
    public int PlayerCleanupDays { get; set; } = 90;

    // Master enable for the Great Pyramid climb solver — when true (default) a
    // walk-to a pyramid room hands off to PyramidSolver; when false the walk
    // fails normally like any unroutable destination. Install-wide (Global tier);
    // read live via PyramidSolver.Enabled. Surfaced in Settings → Other.
    public bool PyramidSolverEnabled { get; set; } = true;

    // Master enable for the random-teleport asylum (maze) solver — when true
    // (default) a walk-to a maze-pocket room hands off to TeleportMazeSolver; when
    // false the walk fails normally. Install-wide (Global tier); read live via
    // TeleportMazeSolver.Enabled. Surfaced in Settings → Other.
    public bool AsylumSolverEnabled { get; set; } = true;

    // Per-tab settings deltas — keyed by tab name (Health / Combat / Talk /
    // etc.). Each value is a partial DTO for that tab containing only the
    // fields the user pinned to the Global tier. SettingsResolver merges
    // these across all four tiers.
    public Dictionary<string, JsonElement>? Settings { get; set; }
}
