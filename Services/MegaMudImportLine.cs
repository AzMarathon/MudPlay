namespace MudPlay.Services;

// One line of a MegaMUD import's review: a setting that was carried over, with the
// value it lands as (Note empty), or one that was left behind, with why (Note set).
// Group is the MudPlay settings tab it belongs under.
//
// A carried-over setting can be changed in the review before the character is made.
// EditKey names the MegaMUD value it came from and EditValue is what its editor
// starts with; what the user leaves there is fed back through the same mapping in
// place of the file's value. Advise gives the caution to show beside a value, asked
// again as the value is edited so it follows what will really be imported.
public sealed record MegaMudImportLine(string Group, string Setting, string Value, string Note = "")
{
    public MegaMudImportEdit Edit { get; init; }
    public string EditKey { get; init; } = string.Empty;
    public string EditValue { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public Func<string, MegaMudImportAdvice?>? Advise { get; init; }

    public bool WasImported => Note.Length == 0;
}
