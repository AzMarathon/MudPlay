using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// How the loaded character runs each saved loop: its two "Entire Loop Settings",
// only attack in lair rooms and the wait to enter lairs. The loops are files with
// the game data, shared by every character on it, and those two rode in the file, so
// one character ticking either changed it for all of them. They are the character's
// own now, kept on the profile by loop name (CharacterProfile.LoopSettings) (user,
// 2026-10-10). What is the loop's stays in the file: its rooms, their commands and
// delays, and the per-room no rest / no attack / rest HP / rest mana.
//
// LoopManager still hands out Loop.OnlyAttackInLairRooms and Loop.LairEntryDebuff, so
// nothing that reads them changed: this store plugs into it and it stamps both.
//
// A character holds a loop's settings from the first time it changes either of them
// (a tick in the nav rail, a Save in the editor or the builder with a different
// value, a loop it accepted from another player). Until then it runs the loop with
// the two the file holds, so every loop runs as it did before the move. Nothing here
// writes the file, so one character's change never reaches another: that one goes on
// reading the file, or its own. Taking them only on a change, and not for every loop
// a character has ever seen, keeps a profile to the handful of loops it has tuned
// (the bundled loops alone are several hundred).
public sealed class LoopSettingsStore
{
    private const string LogCategory = "LoopSettings";

    private readonly ProfileService _profile;
    private readonly LoopManager _loops;
    private readonly LogService? _log;
    private readonly Dictionary<string, LoopOwnSettings> _own = new(StringComparer.OrdinalIgnoreCase);

    public LoopSettingsStore(ProfileService profile, LoopManager loops, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loops);
        _profile = profile;
        _loops = loops;
        _log = log;

        _loops.AttachSettings(OwnFor, Keep, Forget);
        _profile.ProfileLoaded += Hydrate;
        _profile.ProfileClosed += OnProfileClosed;
        if (_profile.Current is { } current) Hydrate(current);
    }

    // Whether the loaded character has settings of its own for the loop named name,
    // as against still running it with the loop file's.
    public bool HasOwn(string name) => _own.ContainsKey(name);

    // Every loop the loaded character has its own settings for, for a bug report.
    public IReadOnlyDictionary<string, LoopOwnSettings> Own => _own;

    private LoopOwnSettings? OwnFor(string name) =>
        _own.TryGetValue(name, out LoopOwnSettings own) ? own : null;

    private void Hydrate(CharacterProfile profile)
    {
        _own.Clear();
        foreach ((string name, LoopSettingsChoice? choice) in profile.LoopSettings ?? new())
        {
            // A hand-edited profile can hold a name with nothing under it.
            if (choice is null) continue;
            _own[name] = new LoopOwnSettings(choice.OnlyAttackInLairRooms, ModeOf(choice.LairEntryDebuff));
        }
        _loops.RefreshSettings();
    }

    private void OnProfileClosed()
    {
        _own.Clear();
        _loops.RefreshSettings();
    }

    private static LairEntryDebuffMode ModeOf(string? name) =>
        Enum.TryParse(name, ignoreCase: true, out LairEntryDebuffMode mode) && Enum.IsDefined(mode)
            ? mode
            : LairEntryDebuffMode.Off;

    // fromFile is the pair the loop file holds: what the character ran the loop with
    // if it has none of its own yet.
    private void Keep(string name, LoopOwnSettings wanted, LoopOwnSettings fromFile)
    {
        if (_profile.Current is not { } current) return;
        bool first = !_own.TryGetValue(name, out LoopOwnSettings had);
        if (first) had = fromFile;
        if (!first && had == wanted) return;

        _own[name] = wanted;
        Dictionary<string, LoopSettingsChoice> kept = current.LoopSettings ??= new();
        RemoveIgnoringCase(kept, name);
        kept[name] = new LoopSettingsChoice
        {
            OnlyAttackInLairRooms = wanted.OnlyAttackInLairRooms,
            LairEntryDebuff = wanted.LairEntryDebuff.ToString(),
        };
        _profile.Save();

        if (first)
            _log?.Info(LogCategory,
                $"loop '{name}': this character had set neither loop setting; both start from the loop file's "
                + $"({Describe(fromFile)}) and are its own from here");
        if (had.OnlyAttackInLairRooms != wanted.OnlyAttackInLairRooms)
            _log?.Info(LogCategory,
                $"loop '{name}': only attack in lair rooms {OnOff(wanted.OnlyAttackInLairRooms)} for this character "
                + $"(was {OnOff(had.OnlyAttackInLairRooms)})");
        if (had.LairEntryDebuff != wanted.LairEntryDebuff)
            _log?.Info(LogCategory,
                $"loop '{name}': wait to enter lairs {LairEntryDebuffModes.Label(wanted.LairEntryDebuff)} for this character "
                + $"(was {LairEntryDebuffModes.Label(had.LairEntryDebuff)})");
    }

    private void Forget(string name)
    {
        if (!_own.Remove(name)) return;
        if (_profile.Current is not { LoopSettings: { } kept }) return;
        RemoveIgnoringCase(kept, name);
        _profile.Save();
        _log?.Info(LogCategory, $"loop '{name}' deleted: this character's settings for it dropped");
    }

    // The profile's own dictionary is keyed as the file spelled the names; loop
    // names are matched without case everywhere else.
    private static void RemoveIgnoringCase(Dictionary<string, LoopSettingsChoice> kept, string name)
    {
        foreach (string key in kept.Keys.Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
            kept.Remove(key);
    }

    public static string Describe(LoopOwnSettings s) =>
        $"only attack in lair rooms {OnOff(s.OnlyAttackInLairRooms)}, wait to enter lairs {LairEntryDebuffModes.Label(s.LairEntryDebuff)}";

    private static string OnOff(bool on) => on ? "on" : "off";
}
