using System.Text.RegularExpressions;

namespace MudPlay.Terminal;

// Recognises a line whose body another player typed — gossip, auction, broadcast,
// telepath, gangpath, yell, say. LineExtractor uses it to keep those lines out of
// the game-output path entirely.
//
// Why this exists: nearly every line-aware subsystem matches server text with a
// substring / loose regex ("You are flat on your back!", "You are blind.", "You
// feel lucky!", a token / charge / death line …). A player can put that exact text
// inside a chat message — or, as in report paradigm-20260921-053754, just relay
// their own game output over a broadcast — and the client reacted as if the
// server had said it (a phantom knockdown latched MovementPrevented, paused the
// loop and re-cast cure paralysis every round with nothing to cure). Chat is
// untrusted input; classifying it once, here, means no consumer has to remember
// to guard against it.
//
// Deliberately LOOSE: matching only the channel prefix (not the closing quote, not a
// well-formed player name) so a malformed or oddly-wrapped chat line still fails
// closed. The optional leading status prompt tolerates a row where the prompt
// wasn't split off by LineExtractor. Server-authored confirmations that carry no
// player-typed body ("--- Telepath sent to X ---", "Server PvP Message: …",
// "X just entered the Realm.") are NOT chat here — they're not spoofable text.
public static partial class ChatLineDetector
{
    public static bool IsChat(string? text)
        => !string.IsNullOrEmpty(text) && ChatShape().IsMatch(text);

    [GeneratedRegex(
        @"^\s*(?:\[HP=[^\]]*\]:(?:\s\((?:Resting|Meditating)\))?\s*)?" +
        @"(?:Broadcast from \S+ """ +
        @"|\S+ (?:gossips|auctions|gangpaths|telepaths):" +
        @"|(?:\S+ yells|You yell) """ +
        @"|(?:\S+ says|You say)(?: \(to [^)]*\))? """ +
        @")",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ChatShape();
}
