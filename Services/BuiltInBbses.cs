using MudPlay.Models.Profile;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// The boards MudPlay comes with: Anomaly Mud and Paradigm. Each is added to the BBS
// list once per install, and a character with no login saved for one of them gets
// its logon steps from here, so a new player only types a username and password.
// No account, character or password ships: the steps send {username} / {password},
// which resolve from the character's own saved credentials at connect time.
public static class BuiltInBbses
{
    public sealed record Preset(string Name, string Host, int Port, string? WebsiteUrl, IReadOnlyList<MenuStep> LogonSteps)
    {
        // Same host and port, whatever the user named the board.
        public bool IsBoardOf(BbsProfile bbs) =>
            bbs.Port == Port && string.Equals(bbs.Host.Trim(), Host, StringComparison.OrdinalIgnoreCase);
    }

    // Paradigm's character pick is left out: it is a slot on the player's own
    // account, so the login stops at the character list for them to choose.
    public static IReadOnlyList<Preset> All { get; } =
    [
        new("Anomaly", "playanomalymud.com", 2324, "https://playanomalymud.com",
        [
            new() { WaitForPattern = "Enter your BBS account name", Send = "{username}" },
            new() { WaitForPattern = "Enter your Password:", Send = "{password}" },
            new() { WaitForPattern = "Selection:", Send = "1" },
        ]),
        new("Paradigm", "paramud.mudinfo.net", 2323, null,
        [
            new() { WaitForPattern = "Please enter your username or ", Send = "{username}" },
            new() { WaitForPattern = "Please enter your password:", Send = "{password}" },
            new() { WaitForPattern = "P : Paradigm", Send = "P" },
            new() { WaitForPattern = "{ Realm Selection }", Send = "1" },
        ]),
    ];

    // The built-in board this BBS is, or null.
    public static Preset? For(BbsProfile bbs) => All.FirstOrDefault(p => p.IsBoardOf(bbs));

    // A fresh copy of the board's logon steps, for a character to own and edit.
    // Empty for a board that isn't built in.
    public static List<MenuStep> LogonStepsFor(BbsProfile bbs) =>
        For(bbs)?.LogonSteps.Select(s => new MenuStep { WaitForPattern = s.WaitForPattern, Send = s.Send }).ToList()
            ?? [];

    // Add each preset not added before, and return the names added. A preset is
    // added once: one the user removed stays removed, and one whose name or board
    // is already in the list (a "Paramud" on Paradigm's host) is only marked done.
    // added is the install's record of presets already handled (GlobalSettings).
    public static IReadOnlyList<string> AddMissing(BbsProfileStore store, ICollection<string> added, IEnumerable<Preset> presets)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(presets);
        List<BbsProfile> existing = store.ListNames().Select(store.Peek).OfType<BbsProfile>().ToList();
        List<string> made = [];
        foreach (Preset preset in presets)
        {
            if (added.Contains(preset.Name, StringComparer.OrdinalIgnoreCase)) continue;
            added.Add(preset.Name);
            if (store.Exists(preset.Name) || existing.Any(preset.IsBoardOf)) continue;
            store.Save(new BbsProfile
            {
                Name = preset.Name,
                Host = preset.Host,
                Port = preset.Port,
                WebsiteUrl = preset.WebsiteUrl,
                Realms = [new RealmProfile { Name = preset.Name }],
            });
            made.Add(preset.Name);
        }
        return made;
    }
}
