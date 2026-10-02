using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Sounds;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// "Sounds" tab — a master switch and volume, then one row per sound cue (level up,
// boss killed, a walk finishing, …) with its own sound, volume and test button.
// Persists as the "Sounds" entry in CharacterProfile.Settings, storing only the
// cues changed from their catalogue defaults; SoundCueEngine reads it live.
public sealed partial class SoundsSectionViewModel : SettingsSectionViewModel
{
    public const string TabKey = "Sounds";

    private readonly ProfileService _profile;
    private readonly Action<string, int> _play;
    private readonly Action _applied;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "sounds";
    public override string Title => "Sounds";
    public override bool IsDirty => _dirty;
    public override Control View => _view ??= new SoundsSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels =>
        new[] { "Sounds", "Sound", "Volume", "Audio", "Mute", "Alert", "Notification" }
            .Concat(SoundCues.All.Select(c => c.Label));

    public bool HasProfile => _profile.Current is not null;

    [ObservableProperty] private bool _soundsEnabled = true;
    // A double for the slider; saved as a whole percent.
    [ObservableProperty] private double _masterVolume = 80;

    public IReadOnlyList<SoundCueGroup> Groups { get; }
    private readonly List<SoundCueRowViewModel> _rows = new();

    public SoundsSectionViewModel() : this(
        AppServices.Current.Profile, AppServices.Current.SoundPlayer.Play, AppServices.Current.Sounds.Invalidate) { }

    // applied tells the cue engine its kept copy of the settings is out of date.
    public SoundsSectionViewModel(ProfileService profile, Action<string, int> play, Action applied)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(applied);
        _profile = profile;
        _play = play;
        _applied = applied;

        SoundSettings settings = Read();
        _suppressDirty = true;
        foreach (SoundCue cue in SoundCues.All)
            _rows.Add(new SoundCueRowViewModel(cue, SoundCueEngine.Resolve(settings, cue), MarkDirty, TestRow));
        Groups = _rows.GroupBy(r => r.Cue.Group)
            .Select(g => new SoundCueGroup(g.Key, g.ToList())).ToList();
        SoundsEnabled = settings.Enabled;
        MasterVolume = Math.Clamp(settings.MasterVolume, 0, 100);
        _suppressDirty = false;

        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += Reload;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= Reload;
        });
    }

    // Plays the row as it stands on screen — unsaved edits included — at the master
    // volume shown, whether or not the cue or the master switch is on.
    private void TestRow(SoundCueRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.Sound)) return;
        _play(row.Sound, MasterPercent * row.VolumePercent / 100);
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;
        SoundSettings dto = new() { Enabled = SoundsEnabled, MasterVolume = MasterPercent };
        foreach (SoundCueRowViewModel row in _rows)
            if (!row.IsDefault) dto.Cues[row.Cue.Id] = row.ToSettings();

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();
        _applied();
        ClearDirty();
    }

    public override void Discard() => Reload();

    private void OnProfileChanged(CharacterProfile _) => Reload();

    private void Reload()
    {
        SoundSettings settings = Read();
        _suppressDirty = true;
        SoundsEnabled = settings.Enabled;
        MasterVolume = Math.Clamp(settings.MasterVolume, 0, 100);
        foreach (SoundCueRowViewModel row in _rows) row.Load(SoundCueEngine.Resolve(settings, row.Cue));
        _suppressDirty = false;
        ClearDirty();
        OnPropertyChanged(nameof(HasProfile));
    }

    private SoundSettings Read()
    {
        if (_profile.Current?.Settings is not { } all || !all.TryGetValue(TabKey, out JsonElement json))
            return new SoundSettings();
        try
        {
            return JsonSerializer.Deserialize<SoundSettings>(json) ?? new SoundSettings();
        }
        catch (JsonException)
        {
            // A malformed entry falls back to the defaults rather than breaking the tab.
            return new SoundSettings();
        }
    }

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void MarkDirty()
    {
        if (_suppressDirty || _dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnSoundsEnabledChanged(bool value) => MarkDirty();
    partial void OnMasterVolumeChanged(double value) => MarkDirty();

    private int MasterPercent => Math.Clamp((int)Math.Round(MasterVolume), 0, 100);
}
