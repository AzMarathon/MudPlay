using Avalonia.Media.Imaging;

namespace MudPlay.ViewModels.Settings;

// One row in the Talk tab's emote list. Surfaces the built-in emotes and the user's own
// staged set: a default can be overridden or hidden ("removed"), a hidden default
// restored, and images imported without a definition are flagged (IsRed) for the user to
// name. Preview is an image thumbnail or the emoji glyph. Action is "Remove" (active) or
// "Restore" (hidden default). DraftId links a staged/imported row back to its EmoteDraft.
public sealed class EmoteRowViewModel
{
    public string Shortcode { get; }
    public string Display => string.IsNullOrEmpty(Shortcode) ? "(set a shortcode)" : $":{Shortcode}:";
    public bool IsImage { get; }
    public bool IsText => !IsImage;
    public string PreviewText { get; }
    public Bitmap? PreviewImage { get; }
    public string Source { get; }
    public string ActionLabel { get; }
    public bool IsRed { get; }
    public string? DraftId { get; }

    public EmoteRowViewModel(string shortcode, bool isImage, string previewText, Bitmap? previewImage,
                             string source, string actionLabel, bool isRed = false, string? draftId = null)
    {
        Shortcode = shortcode;
        IsImage = isImage;
        PreviewText = previewText;
        PreviewImage = previewImage;
        Source = source;
        ActionLabel = actionLabel;
        IsRed = isRed;
        DraftId = draftId;
    }
}
