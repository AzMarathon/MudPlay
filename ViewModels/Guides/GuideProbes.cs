namespace MudPlay.ViewModels.Guides;

// The live app state guide steps read, injected so the catalogue stays testable.
// CharacterLoaded: a named character is the loaded profile.
internal sealed record GuideProbes(
    Func<bool> HasGameData, Func<bool> HasBbs, Func<bool> HasCharacter,
    Func<bool> CharacterLoaded, Func<bool> IsConnected)
{
    // The setup tour auto-shows on launch while one of these is missing.
    public bool AnyPrerequisiteMissing => !HasGameData() || !HasBbs() || !HasCharacter();
}
