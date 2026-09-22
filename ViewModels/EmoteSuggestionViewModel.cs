using Avalonia.Media.Imaging;

namespace MudPlay.ViewModels;

// One entry in the conversation input's ":" emote picker: the shortcode + a preview
// (image thumbnail or emoji glyph).
public sealed class EmoteSuggestionViewModel
{
    public string Shortcode { get; }
    public string Display => $":{Shortcode}:";
    public bool IsImage { get; }
    public bool IsText => !IsImage;
    public string PreviewText { get; }
    public Bitmap? PreviewImage { get; }

    public EmoteSuggestionViewModel(string shortcode, bool isImage, string previewText, Bitmap? previewImage)
    {
        Shortcode = shortcode;
        IsImage = isImage;
        PreviewText = previewText;
        PreviewImage = previewImage;
    }
}
