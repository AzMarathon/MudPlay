using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace MudPlay.Views.Settings;

// The "pick a sound file" dialog shared by the Sounds tab and the Event editor.
internal static class SoundFilePicker
{
    // The chosen file's path, or null when the picker was cancelled.
    public static async Task<string?> PickAsync(Control owner, string title)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top) return null;
        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Sound files") { Patterns = new[] { "*.wav", "*.mp3", "*.ogg", "*.flac" } },
                FilePickerFileTypes.All,
            },
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
