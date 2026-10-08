namespace MudPlay.Services;

// How a carried-over setting is edited in the import review.
public enum MegaMudImportEdit
{
    // Shown, not edited (a summary line, or a setting that was left behind).
    None,
    // On / off.
    Flag,
    // A whole number, with Unit after it.
    Number,
    // Free text: a spell's short code, a command.
    Text,
}
