using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Sounds;

// Decides whether a cue plays and how loud, then hands it to the player. Holds no
// audio code: play is the player's entry point, which returns at once and does the
// work off the calling thread, so a cue costs the caller a dictionary lookup.
//
// UI thread only, like the signals that feed it.
//
// Settings are read through a delegate once and kept until Invalidate, so a cue on
// a busy path (every kill counts toward the kill milestone) never re-parses the
// profile. A cue the user never touched takes its catalogue default. Each cue is held to one play per MinGap so a burst (a
// room of monsters dying together, a flood of telepaths) is one sound.
public sealed class SoundCueEngine
{
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(1);

    private readonly Func<SoundSettings> _settings;
    private readonly Action<string, int> _play;
    private readonly Func<DateTimeOffset> _clock;
    private readonly LogService? _log;
    private readonly Dictionary<string, DateTimeOffset> _lastPlayed = new(StringComparer.Ordinal);
    private SoundSettings? _cached;
    private int _kills;

    // play takes the sound (a "tone:" name or a file path) and a 0–100 volume.
    public SoundCueEngine(
        Func<SoundSettings> settings, Action<string, int> play, Func<DateTimeOffset>? clock = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(play);
        _settings = settings;
        _play = play;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _log = log;
    }

    // The cue's effective setting: the user's entry, or the catalogue default.
    public static SoundCueSettings Resolve(SoundSettings settings, SoundCue cue) =>
        settings.Cues.TryGetValue(cue.Id, out SoundCueSettings? set)
            ? set
            : new SoundCueSettings { Enabled = false, Sound = cue.DefaultSound, Volume = 100, Every = cue.DefaultEvery };

    // Master volume × the cue's own, as 0–100.
    public static int EffectiveVolume(SoundSettings settings, SoundCueSettings cue) =>
        Math.Clamp(settings.MasterVolume, 0, 100) * Math.Clamp(cue.Volume, 0, 100) / 100;

    // The saved settings changed, or another character loaded.
    public void Invalidate() => _cached = null;

    private SoundSettings Settings => _cached ??= _settings();

    // Play the cue's sound, if sounds and the cue are on.
    public void Fire(string cueId) => Fire(cueId, soundOverride: null);

    // A sound named by the thing that fired (a trigger's file, an event's pick),
    // gated and levelled by its cue.
    public void FireWith(string cueId, string sound) => Fire(cueId, soundOverride: sound);

    // An editor's test button: the sound at the cue's volume, whatever the switches
    // say and with no gap.
    public void Preview(string cueId, string sound)
    {
        if (SoundCues.Find(cueId) is not { } cue || string.IsNullOrWhiteSpace(sound)) return;
        int volume = EffectiveVolume(Settings, Resolve(Settings, cue));
        if (volume > 0) _play(sound, volume);
    }

    private void Fire(string cueId, string? soundOverride)
    {
        if (SoundCues.Find(cueId) is not { } cue) return;
        SoundSettings settings = Settings;
        if (!settings.Enabled) return;
        SoundCueSettings set = Resolve(settings, cue);
        if (!set.Enabled) return;
        string sound = soundOverride ?? set.Sound;
        if (string.IsNullOrWhiteSpace(sound)) return;
        int volume = EffectiveVolume(settings, set);
        if (volume <= 0) return;

        // A named sound is throttled per sound, so two different triggers firing
        // together both play.
        string gapKey = soundOverride is null ? cueId : cueId + "|" + soundOverride;
        DateTimeOffset now = _clock();
        if (_lastPlayed.TryGetValue(gapKey, out DateTimeOffset last) && now - last < MinGap) return;
        _lastPlayed[gapKey] = now;
        _log?.Debug("Sounds", $"{cueId}: {sound} at {volume}%");
        _play(sound, volume);
    }

    // A loop lap finished: fires the loop milestone on every Nth lap. The count is the
    // loop runner's own, which carries on across a detour and restarts with the loop.
    public void NoteLap(int completedLaps) => NoteCount(completedLaps, SoundCues.LoopMilestone);

    // A monster died: fires the kill milestone on every Nth kill since the character
    // loaded.
    public void NoteKill() => NoteCount(++_kills, SoundCues.KillMilestone);

    // Another character loaded: its own settings, and kills from zero.
    public void ResetCounts()
    {
        _kills = 0;
        Invalidate();
    }

    private void NoteCount(int count, string cueId)
    {
        if (count <= 0 || SoundCues.Find(cueId) is not { } cue) return;
        int every = Resolve(Settings, cue).Every;
        if (every > 0 && count % every == 0) Fire(cueId);
    }
}
