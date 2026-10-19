namespace MudPlay.Services;

// The values a number kept in a settings file may hold. SettingsValueGuard pulls
// a number read from global.json, a bbs.json or a profile.json back into this
// range as the file is read, before anything else sees it, so a hand edit, a bad
// import or a damaged save can't hand the code that sizes a buffer, counts a loop
// or builds a date a value it was never meant to see. On a list, or a dictionary,
// it bounds each element.
//
// Where the Settings window bounds a field, the range is the window's, widened
// only to take in a default the window doesn't offer (0 for "the cue's default"),
// so a value the user can set is never moved; SettingRangeCoverageTests holds the
// two together. Elsewhere it comes from what the code does with the value, and is
// wide enough that nothing the game or the client writes falls outside it.
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingRangeAttribute(double min, double max) : Attribute
{
    public double Min { get; } = min;
    public double Max { get; } = max;
}
