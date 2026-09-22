using Avalonia.Media.Imaging;

namespace MudPlay.ViewModels.Settings;

// One row in the Talk tab's emote list: the shortcode, a preview (image thumbnail or
// emoji glyph), and where it comes from. The list surfaces the built-in emotes as well
// as the user's own, so a default can be overridden (add a custom emote of the same
// shortcode) and a custom one removed (reverting to the default if it shadowed one).
public sealed class EmoteRowViewModel
{
    public string Shortcode { get; }
    public string Display => $":{Shortcode}:";
    public bool IsImage { get; }
    public bool IsText => !IsImage;
    public string PreviewText { get; }
    public Bitmap? PreviewImage { get; }

    // "Default", "Custom", or "Custom (overrides default)".
    public string Source { get; }
    // True for user-defined emotes — only those show a Remove button.
    public bool CanRemove { get; }

    public EmoteRowViewModel(string shortcode, bool isImage, string previewText, Bitmap? previewImage,
                             string source, bool canRemove)
    {
        Shortcode = shortcode;
        IsImage = isImage;
        PreviewText = previewText;
        PreviewImage = previewImage;
        Source = source;
        CanRemove = canRemove;
    }
}
