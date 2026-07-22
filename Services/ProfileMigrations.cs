using System.Text.Json;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// One-time, per-profile schema upgrades. Applied on load (ProfileService.Load)
// before ProfileLoaded fires, so per-character services see the migrated shape.
// Each step is gated on CharacterProfile.SchemaVersion and bumps it, so a
// profile migrates exactly once and re-running Apply is a no-op.
public static class ProfileMigrations
{
    // Bring profile up to CharacterProfile.CurrentSchemaVersion. Returns true
    // when anything changed, so the caller can persist the upgraded profile.
    public static bool Apply(CharacterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        bool changed = false;

        // v1 → v2: the default keybindings and toolbar layout were overhauled.
        // Reset every existing profile onto the new defaults by dropping its
        // stored keybind + toolbar-layout deltas. Auto-mode and all other
        // settings are deliberately left untouched.
        if (profile.SchemaVersion < 2)
        {
            if (profile.BuiltInKeybindings is not null)
                profile.BuiltInKeybindings = null;
            ResetToolbarLayout(profile);
            profile.SchemaVersion = 2;
            changed = true;
        }

        return changed;
    }

    // Null out the Layout inside the profile's stored "Toolbar" settings section
    // so it falls back to ToolbarDefaults, while preserving the user's Visible /
    // Position choices. No-op when the profile has no Toolbar section (already
    // on defaults).
    private static void ResetToolbarLayout(CharacterProfile profile)
    {
        if (profile.Settings is not { } settings) return;
        if (!settings.TryGetValue("Toolbar", out JsonElement json)) return;

        ToolbarSettings? dto;
        try { dto = JsonSerializer.Deserialize<ToolbarSettings>(json.GetRawText()); }
        catch { dto = null; }
        if (dto?.Layout is null) return;

        dto.Layout = null;
        settings["Toolbar"] = JsonSerializer.SerializeToElement(dto);
    }
}
