using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Sounds;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.Settings;

// One sound cue on the Sounds tab: whether it plays, which sound, how loud, and for
// the counted cues how often. The sound is one of the built-in tones or the user's
// own file — picking "Custom file…" shows the path box and its Browse button.
public sealed partial class SoundCueRowViewModel : ObservableObject
{
    public const string CustomFileLabel = "Custom file…";

    private readonly Action _changed;
    private readonly Action<SoundCueRowViewModel> _test;

    public SoundCue Cue { get; }
    public string Label => Cue.Label;
    public string Description => Cue.Description;

    // The counted cues carry an "every N" box.
    public bool HasEvery => Cue.DefaultEvery > 0;

    // False for the cues whose triggers / events each name their own sound: the row
    // has nothing to pick.
    public bool HasSound => Cue.HasOwnSound;

    public IReadOnlyList<string> SoundOptions { get; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCustomFile))] private string _selectedSound;
    [ObservableProperty] private string _customFile = string.Empty;
    // A double because the slider it binds to is one; whole numbers only (the slider
    // snaps), saved as an int.
    [ObservableProperty] private double _volume = 100;
    [ObservableProperty] private int _every;

    public bool IsCustomFile => HasSound && SelectedSound == CustomFileLabel;

    public SoundCueRowViewModel(SoundCue cue, SoundCueSettings set, Action changed, Action<SoundCueRowViewModel> test)
    {
        Cue = cue;
        _changed = changed;
        _test = test;
        SoundOptions = SoundTones.All.Select(t => t.Label).Append(CustomFileLabel).ToArray();
        _selectedSound = SoundOptions[0];
        Load(set);
    }

    public void Load(SoundCueSettings set)
    {
        Enabled = set.Enabled;
        Volume = Math.Clamp(set.Volume, 0, 100);
        Every = set.Every > 0 ? set.Every : Cue.DefaultEvery;
        string? tone = SoundTones.All.FirstOrDefault(t => t.Id == set.Sound).Label;
        if (tone is not null || string.IsNullOrWhiteSpace(set.Sound))
        {
            SelectedSound = tone ?? SoundOptions[0];
            CustomFile = string.Empty;
        }
        else
        {
            SelectedSound = CustomFileLabel;
            CustomFile = set.Sound;
        }
    }

    // The sound as it is stored: a tone id or the file path.
    public string Sound =>
        !HasSound ? string.Empty
        : IsCustomFile ? CustomFile.Trim()
        : SoundTones.All.FirstOrDefault(t => t.Label == SelectedSound).Id ?? Cue.DefaultSound;

    public int VolumePercent => Math.Clamp((int)Math.Round(Volume), 0, 100);

    public SoundCueSettings ToSettings() => new()
    {
        Enabled = Enabled, Sound = Sound, Volume = VolumePercent,
        Every = HasEvery ? Math.Max(1, Every) : 0,
    };

    // True when the row is exactly the catalogue default, so nothing need be saved.
    public bool IsDefault =>
        !Enabled && Sound == Cue.DefaultSound && VolumePercent == 100
        && (!HasEvery || Every == Cue.DefaultEvery);

    [RelayCommand]
    private void Test() => _test(this);

    partial void OnEnabledChanged(bool value) => _changed();
    partial void OnSelectedSoundChanged(string value) => _changed();
    partial void OnCustomFileChanged(string value) => _changed();
    partial void OnVolumeChanged(double value) => _changed();
    partial void OnEveryChanged(int value) => _changed();
}
