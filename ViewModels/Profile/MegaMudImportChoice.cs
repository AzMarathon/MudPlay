namespace MudPlay.ViewModels.Profile;

// What the user settled on in the MegaMUD import review: the new character's name,
// whether the file's BBS user ID and password come with it, and whether its redial
// and cleanup settings are written onto the BBS.
public sealed record MegaMudImportChoice(string Name, bool ImportLogin, bool ApplyBbsSettings,
    IReadOnlyDictionary<string, string> Edits);
