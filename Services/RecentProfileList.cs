using MudPlay.Models.Settings;

namespace MudPlay.Services;

// Global-tier recent-profiles bookkeeping shared by every surface that renames a
// BBS — Settings → BBS + Display and the Profile Management window both need the
// same cascade, so it lives here rather than duplicated on each caller.
internal static class RecentProfileList
{
    // Rewrite the recent-profiles + last-used pointers that named oldBbs to
    // newBbs. They're (bbs, char) refs; without the rewrite the File → Recent
    // menu and startup auto-load point at a BBS folder that no longer exists.
    // Saving fires GlobalSettingsChanged so the live menu rebuilds.
    public static void RekeyBbs(SettingsService settings, string oldBbs, string newBbs)
    {
        if (settings is null) return;
        GlobalSettings current = settings.Current;
        bool changed = false;

        if (current.RecentProfiles is { } recents)
        {
            for (int i = 0; i < recents.Count; i++)
            {
                if (string.Equals(recents[i].Bbs, oldBbs, StringComparison.OrdinalIgnoreCase))
                {
                    recents[i] = recents[i] with { Bbs = newBbs };
                    changed = true;
                }
            }
        }

        if (current.LastUsedProfile is { } last
            && string.Equals(last.Bbs, oldBbs, StringComparison.OrdinalIgnoreCase))
        {
            current.LastUsedProfile = last with { Bbs = newBbs };
            changed = true;
        }

        if (changed) settings.Save();
    }
}
