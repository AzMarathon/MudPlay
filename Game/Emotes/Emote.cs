namespace MudPlay.Game.Emotes;

// Whether an emote renders as a Unicode emoji string (drawn by the font) or as an
// image resource. Built-in image emotes carry an avares:// URI; user-supplied ones
// (a later PR) will carry a file path — the renderer branches on the payload scheme.
public enum EmoteKind { Unicode, Image }

// One emote: a shortcode mapped to either a Unicode emoji string (Kind=Unicode) or an
// image resource path (Kind=Image). Shortcode is the bare name without the surrounding
// colons for a word emote (":lol:" → "lol"), or the literal emoticon (":)" ) for the
// emoticon set. DisplayName is a friendly label for a future picker.
public sealed record Emote(string Shortcode, EmoteKind Kind, string Payload, string DisplayName);
