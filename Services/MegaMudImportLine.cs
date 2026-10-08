namespace MudPlay.Services;

// One line of a MegaMUD import's review: a setting that was carried over, with the
// value it lands as (Note empty), or one that was left behind, with why (Note set).
// Group is the MudPlay settings tab it belongs under.
public sealed record MegaMudImportLine(string Group, string Setting, string Value, string Note = "")
{
    public bool WasImported => Note.Length == 0;
}
