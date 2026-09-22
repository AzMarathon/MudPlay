using System.Collections.Generic;

namespace MudPlay.Game.Emotes;

// The set of emotes the conversation window can substitute: word shortcodes
// (":lol:"), classic emoticons (":)"), and image emotes (the bundled Pepe set). The
// built-in catalog is static data; a later PR layers user-defined emotes on top.
//
// Word shortcodes and image emotes share one case-insensitive name map (so ":monkaS:"
// and ":monkas:" resolve alike). Emoticons are matched case-sensitively as literals
// (":D" ≠ ":d:") and kept in their own map since they aren't colon-delimited names.
public sealed class EmoteCatalog
{
    private readonly Dictionary<string, Emote> _shortcodes = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Emote> _emoticons = new(System.StringComparer.Ordinal);

    // name (no colons, case-insensitive) → emote, for ":name:" substitution.
    public IReadOnlyDictionary<string, Emote> Shortcodes => _shortcodes;
    // literal emoticon (":)" ) → emote, matched case-sensitively.
    public IReadOnlyDictionary<string, Emote> Emoticons => _emoticons;

    private void AddWord(string name, string emoji, string display)
        => _shortcodes[name] = new Emote(name, EmoteKind.Unicode, emoji, display);

    private void AddImage(string name, string file, string display)
        => _shortcodes[name] = new Emote(name, EmoteKind.Image, $"avares://MudPlay/Assets/Emotes/{file}.png", display);

    private void AddEmoticon(string literal, string emoji, string display)
        => _emoticons[literal] = new Emote(literal, EmoteKind.Unicode, emoji, display);

    public bool TryGetShortcode(string name, out Emote emote) => _shortcodes.TryGetValue(name, out emote!);
    public bool TryGetEmoticon(string literal, out Emote emote) => _emoticons.TryGetValue(literal, out emote!);

    // The static built-in catalog: common Unicode word shortcodes + emoticons + the
    // bundled Pepe image set. Built once.
    public static EmoteCatalog BuiltIn { get; } = BuildBuiltIn();

    private static EmoteCatalog BuildBuiltIn()
    {
        EmoteCatalog c = new();

        // ----- Classic emoticons (case-sensitive literals) -----------------
        c.AddEmoticon(":)",  "🙂", "slight smile");
        c.AddEmoticon(":-)", "🙂", "slight smile");
        c.AddEmoticon(":(",  "🙁", "slight frown");
        c.AddEmoticon(":-(", "🙁", "slight frown");
        c.AddEmoticon(":D",  "😀", "grin");
        c.AddEmoticon(":-D", "😀", "grin");
        c.AddEmoticon(";)",  "😉", "wink");
        c.AddEmoticon(";-)", "😉", "wink");
        c.AddEmoticon(":P",  "😛", "tongue");
        c.AddEmoticon(":-P", "😛", "tongue");
        c.AddEmoticon(":p",  "😛", "tongue");
        c.AddEmoticon(":'(", "😢", "crying");
        c.AddEmoticon(":o",  "😮", "surprised");
        c.AddEmoticon(":O",  "😮", "surprised");
        c.AddEmoticon(":|",  "😐", "neutral");
        c.AddEmoticon(":*",  "😘", "kiss");
        c.AddEmoticon("xD",  "😆", "laughing");
        c.AddEmoticon("XD",  "😆", "laughing");
        c.AddEmoticon(">:(", "😠", "angry");
        c.AddEmoticon("8)",  "😎", "cool");
        c.AddEmoticon("<3",  "❤️", "heart");
        c.AddEmoticon("</3", "💔", "broken heart");

        // ----- Word shortcodes (Unicode) -----------------------------------
        c.AddWord("smile", "🙂", "slight smile");
        c.AddWord("grin", "😁", "grin");
        c.AddWord("joy", "😂", "tears of joy");
        c.AddWord("lol", "😂", "tears of joy");
        c.AddWord("rofl", "🤣", "rolling on the floor");
        c.AddWord("laughing", "😆", "laughing");
        c.AddWord("wink", "😉", "wink");
        c.AddWord("cry", "😢", "crying");
        c.AddWord("sob", "😭", "sobbing");
        c.AddWord("sad", "🙁", "sad");
        c.AddWord("frowning", "🙁", "sad");
        c.AddWord("heart", "❤️", "heart");
        c.AddWord("heart_eyes", "😍", "heart eyes");
        c.AddWord("kiss", "😘", "kiss");
        c.AddWord("thumbsup", "👍", "thumbs up");
        c.AddWord("+1", "👍", "thumbs up");
        c.AddWord("thumbsdown", "👎", "thumbs down");
        c.AddWord("-1", "👎", "thumbs down");
        c.AddWord("ok_hand", "👌", "ok hand");
        c.AddWord("clap", "👏", "clap");
        c.AddWord("pray", "🙏", "pray");
        c.AddWord("wave", "👋", "wave");
        c.AddWord("muscle", "💪", "flex");
        c.AddWord("eyes", "👀", "eyes");
        c.AddWord("thinking", "🤔", "thinking");
        c.AddWord("shrug", "🤷", "shrug");
        c.AddWord("facepalm", "🤦", "facepalm");
        c.AddWord("fire", "🔥", "fire");
        c.AddWord("100", "💯", "hundred");
        c.AddWord("poop", "💩", "poop");
        c.AddWord("skull", "💀", "skull");
        c.AddWord("tada", "🎉", "tada");
        c.AddWord("rocket", "🚀", "rocket");
        c.AddWord("star", "⭐", "star");
        c.AddWord("sunglasses", "😎", "cool");
        c.AddWord("cool", "😎", "cool");
        c.AddWord("sweat", "😅", "nervous");
        c.AddWord("angry", "😠", "angry");
        c.AddWord("rage", "😡", "rage");
        c.AddWord("sleeping", "😴", "sleeping");
        c.AddWord("vomit", "🤮", "vomit");
        c.AddWord("clown", "🤡", "clown");
        c.AddWord("ghost", "👻", "ghost");
        c.AddWord("alien", "👽", "alien");
        c.AddWord("robot", "🤖", "robot");
        c.AddWord("beer", "🍺", "beer");
        c.AddWord("coffee", "☕", "coffee");
        c.AddWord("pizza", "🍕", "pizza");
        c.AddWord("crown", "👑", "crown");
        c.AddWord("gg", "🎮", "good game");
        c.AddWord("wink2", "😜", "playful wink");

        // ----- Bundled Pepe image emotes -----------------------------------
        // Distributable set the user supplied; downscaled into Assets/Emotes.
        c.AddImage("monkas", "monkas", "monkaS");
        c.AddImage("pepecry", "pepecry", "pepe cry");
        c.AddImage("sadge", "sadge", "Sadge");
        c.AddImage("sadge2", "sadge2", "sadge (small)");
        c.AddImage("pepejesus", "pepejesus", "pepe with jesus");
        c.AddImage("copium", "copium", "Copium");
        c.AddImage("pepeclown", "pepeclown", "PepeClown");
        c.AddImage("pepeclown2", "pepeclown2", "pepe clown");
        c.AddImage("pepehands", "pepehands", "PepeHands");
        c.AddImage("sadgepray", "sadgepray", "sadge pray");
        c.AddImage("peperage", "peperage", "pepe rage");
        c.AddImage("pepeno", "pepeno", "pepe no-sign");
        c.AddImage("pepeok", "pepeok", "pepe ok");
        c.AddImage("pepethink", "pepethink", "pepe think");
        c.AddImage("pepedeath", "pepedeath", "pepe death");
        c.AddImage("prayge", "prayge", "Prayge");
        c.AddImage("pepecringe", "pepecringe", "pepe cringe");
        c.AddImage("pepehmm", "pepehmm", "pepe hmm");
        c.AddImage("clownge", "clownge", "clownge");
        c.AddImage("poggies", "poggies", "POGGIES");

        return c;
    }
}
