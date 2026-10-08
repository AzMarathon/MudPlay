namespace MudPlay.Services;

// A caution shown with a carried-over setting in the import review. Strong marks the
// case the user should act on before importing, rather than just look over.
public sealed record MegaMudImportAdvice(string Text, bool Strong);
