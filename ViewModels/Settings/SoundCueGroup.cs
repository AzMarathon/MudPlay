namespace MudPlay.ViewModels.Settings;

// A titled run of cue rows on the Sounds tab.
public sealed record SoundCueGroup(string Title, IReadOnlyList<SoundCueRowViewModel> Rows);
