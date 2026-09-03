namespace MudPlay.Models.Settings;

// Route Details window: colour each monster's name by its live "Hits You %" — the
// same Monster Intel weighted incoming-hit figure — instead of by alignment. A low
// chance-to-hit-you reads green (safe), a high one red (dangerous), with an amber
// middle band; the two boundaries are user-adjustable. Install-wide (Global tier):
// a display preference the user sets from the Details window's own controls. A null
// on GlobalSettings means off, with the factory 15 / 45 split.
public sealed class MonsterHitColorSettings
{
    // Colour monsters by Hits-You-% (default off → the prior alignment tint).
    public bool Enabled { get; set; }

    // Green→amber boundary (%): a monster whose hit% is at or below this reads green.
    public int GreenMax { get; set; } = DefaultGreenMax;

    // Amber→red boundary (%): above GreenMax and at or below this reads amber; above
    // this reads red.
    public int YellowMax { get; set; } = DefaultYellowMax;

    public const int DefaultGreenMax = 15;
    public const int DefaultYellowMax = 45;
}
