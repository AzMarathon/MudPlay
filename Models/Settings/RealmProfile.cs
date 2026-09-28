namespace MudPlay.Models.Settings;

// One realm on a BBS. A board can host several versions of the game, picked from
// its menu, and each usually differs — often with its own MDB export — so what a
// realm needs (its game data, menu commands, death floor, cleanup time, currency
// name) lives here, and everything the client collects while playing it (players
// seen, blacklist, leaderboard, roomba, quests, realm-tier game-data edits, boss
// timers) is stored in its folder (AppPaths.RealmFolder). Character profiles are
// assigned to a realm by name (CharacterProfile.Realm); characters on the same
// realm share its data.
public sealed class RealmProfile
{
    // The user's name for the realm, unique on its BBS; also the folder name.
    public string Name { get; set; } = string.Empty;

    // Imported game-data set folder this realm uses. null falls back to
    // GlobalSettings.DefaultGameDataSet. Also writable from File → Game Data →
    // Active set.
    public string? ActiveGameDataSet { get; set; }

    // ----- Game-menu commands -----
    // The commands the client sends at the MajorMUD main menu to enter / leave
    // the game. Defaults are the standard MajorMUD picks.

    // Sent at the main menu to enter the game. Consumed by MainMenuEntryAutomation
    // when the client detects the main menu after a (re)connect.
    public string GameEntryCommand { get; set; } = "E";

    // Sent at the main menu to log off. Fired by HangupHandler on a permitted
    // @hangup and by the cleanup-warning logout flow.
    public string GameExitCommand { get; set; } = "=x";

    // ----- Realm mechanics -----

    // The negative-HP floor at which a character actually dies. Hitting 0 HP only
    // *drops* you (bleeding out — can't move/fight/cast, but revivable and still
    // able to hang up); death happens when HP falls to this value. A realm balance
    // knob, not a per-character stat. Seeded at the standard -25; clamp consumers
    // treat any positive value as 0. The emergency auto-hangup reads this so it
    // keeps firing through the whole bleeding-out window.
    public int PlayerDiesAtHp { get; set; } = -25;

    // Let the client trace the realm's true death floor from observed *slow*
    // deaths and refine PlayerDiesAtHp toward it. The seed (-25) is only a guess; a
    // bleed-out crosses the floor one tick at a time and lands right at it, so its
    // HP reading is an accurate measurement. Off pins the user's manual value.
    public bool AutoRefineDeathFloor { get; set; } = true;

    // The realm's nightly cleanup wall-clock time. Some bosses ("Respawns @
    // Cleanup" in the boss table) reset only at this daily cleanup — a marked one
    // reads DEAD until the next cleanup, then ALIVE. Format "HH:mm" in
    // CleanupTimeZoneId's zone. Blank disables cleanup-boss timers.
    public string CleanupTimeOfDay { get; set; } = "21:00";

    // Time-zone id CleanupTimeOfDay is expressed in. Defaults to the computer's own
    // zone; invalid / empty falls back to the local zone.
    public string CleanupTimeZoneId { get; set; } = TimeZoneInfo.Local.Id;

    // Name for the top (runic) denomination. Some realms relabel "runic" to a
    // realm-specific word — which changes the coin wording the server sends AND the
    // bare keyword the client keys currency commands on. Blank/whitespace falls
    // back to "runic". Consumed via CurrencyNaming.
    public string RunicCurrencyName { get; set; } = "runic";

    public RealmProfile Clone() => (RealmProfile)MemberwiseClone();
}
