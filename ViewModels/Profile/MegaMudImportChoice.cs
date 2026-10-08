namespace MudPlay.ViewModels.Profile;

// What the user settled on in the MegaMUD import review: the new character's name,
// and whether the file's BBS user ID and password come with it.
public sealed record MegaMudImportChoice(string Name, bool ImportLogin);
