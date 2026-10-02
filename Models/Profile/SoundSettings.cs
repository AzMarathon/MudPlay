using System.Collections.Generic;

namespace MudPlay.Models.Profile;

// Settings → Sounds: a master switch and volume, plus one entry per sound cue the
// user has changed. A cue with no entry here plays as its catalogue default
// (Game.Sounds.SoundCues), so a new cue added later needs no migration. Persists
// as the "Sounds" entry in CharacterProfile.Settings; SoundCueEngine reads it live
// through a delegate, so an edit takes effect without a reconnect.
public sealed class SoundSettings
{
    // Master switch: off silences every cue and the per-trigger sounds.
    public bool Enabled { get; set; } = true;

    // 0–100. Every cue's own volume scales against it.
    public int MasterVolume { get; set; } = 80;

    // Keyed by cue id.
    public Dictionary<string, SoundCueSettings> Cues { get; set; } = new();
}

// One cue as the user set it. Sound is a built-in tone ("tone:chime") or the path
// of a sound file. Every is the milestone size for the counted cues (every N
// loops / kills) and unused by the rest.
public sealed class SoundCueSettings
{
    public bool Enabled { get; set; }
    public string Sound { get; set; } = string.Empty;
    public int Volume { get; set; } = 100;
    public int Every { get; set; }
}
