namespace MudPlay.Models.Profile;

// What the Round Totals window shows, set from the window itself and kept apart
// from the terminal's round table (Settings → Combat → Display): which kinds of
// row, and whether same-named monsters share a row or get one each. Char-tier.
public sealed class RoundTotalsWindowSettings
{
    // The key under CharacterProfile.Settings.
    public const string SectionKey = "RoundTotalsWindow";

    public bool ShowSelf { get; set; } = true;
    public bool ShowParty { get; set; } = true;
    public bool ShowPlayers { get; set; } = true;
    public bool ShowMonsters { get; set; } = true;

    // False stacks same-named monsters on one row ("muckworm x3"); true gives each
    // its own ("muckworm #1", "#2"…).
    public bool EachMonster { get; set; }

    // Count a monster's damage taken, and its dealer's damage dealt, only up to the
    // HP it had left. The window's own choice: the terminal table and Session Stats
    // follow Settings → Combat "Cap at monster HP".
    public bool CapAtMonsterHp { get; set; }
}
