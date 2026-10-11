namespace MudPlay.Game.Simulation;

// Whose results the Simulator holds. A ranking is one character's stats and gear
// played over one game-data set's rooms, so it is no other pairing's: another set's
// room numbers are different rooms, and another character earns and survives
// differently. Character is the loaded profile (its BBS and profile name), null
// while none is; both parts match without case, as set and profile names do.
public readonly record struct SimulatorOwner(string? Set, string? Character)
{
    public bool Equals(SimulatorOwner other) =>
        string.Equals(Set, other.Set, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Character, other.Character, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        Set is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Set),
        Character is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Character));

    public string Label => $"game data '{Set ?? "(none)"}', character '{Character ?? "(none)"}'";

    // The Character part for a profile: its name under its BBS, since one name on
    // two BBSes is two characters. Null for the default profile, which has no name
    // and is no character whichever BBS it was started on.
    public static string? CharacterKey(string? bbs, string? profile) =>
        profile is null ? null : $"{bbs}/{profile}";
}
