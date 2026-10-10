using MudPlay.Models.Profile;

namespace MudPlay.Game.Map;

// The room-spell overlay mode against the two profile keys it is stored in.
//
// NavSpellMode is read by name, and a name the reading client doesn't know fails
// the whole profile load. Clients from before the by-teleport mode know three
// names, so that mode is never written there: its name goes in NavSpellOverlay, a
// string an older client skips, while NavSpellMode is left on a mode it can paint.
// Read the other way, a name in NavSpellOverlay this client doesn't know (a later
// client's mode) falls back to NavSpellMode the same.
public static class SpellDisplayModes
{
    public static SpellDisplayMode Read(CharacterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Enum.TryParse(profile.NavSpellOverlay, out SpellDisplayMode mode) && Enum.IsDefined(mode)
            ? mode
            : profile.NavSpellMode;
    }

    // False when the profile already held the mode, so nothing needs saving.
    public static bool Write(CharacterProfile profile, SpellDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(profile);
        bool everyClientKnows = mode is SpellDisplayMode.Mono or SpellDisplayMode.ByName or SpellDisplayMode.Off;
        // Mono is the nearest an older client can show: spell rooms still marked.
        SpellDisplayMode stored = everyClientKnows ? mode : SpellDisplayMode.Mono;
        string? overlay = everyClientKnows ? null : mode.ToString();
        if (profile.NavSpellMode == stored && profile.NavSpellOverlay == overlay) return false;
        profile.NavSpellMode = stored;
        profile.NavSpellOverlay = overlay;
        return true;
    }
}
