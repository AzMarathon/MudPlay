using System.Collections.Generic;

namespace MudPlay.Game.Emotes;

// The set of emotes the conversation window can substitute: word shortcodes
// (":lol:"), classic emoticons (":)"), and image emotes (the bundled Pepe set). The
// built-in catalog is static data; WithUser layers the user's own emotes on top.
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

    private void AddWord(string name, string emoji)
        => _shortcodes[name] = new Emote(name, EmoteKind.Unicode, emoji);

    private void AddImage(string name, string file)
        => _shortcodes[name] = new Emote(name, EmoteKind.Image, $"avares://MudPlay/Assets/Emotes/{file}.png");

    private void AddEmoticon(string literal, string emoji)
        => _emoticons[literal] = new Emote(literal, EmoteKind.Unicode, emoji);

    public bool TryGetShortcode(string name, out Emote emote) => _shortcodes.TryGetValue(name, out emote!);
    public bool TryGetEmoticon(string literal, out Emote emote) => _emoticons.TryGetValue(literal, out emote!);

    // Add / replace a word shortcode (user-defined emote). A user emote overrides a
    // built-in one of the same name — the merged catalog is built built-in-first.
    public void Upsert(Emote emote) => _shortcodes[emote.Shortcode] = emote;

    // The static built-in catalog: common Unicode word shortcodes + emoticons + the
    // bundled Pepe image set. Built once.
    public static EmoteCatalog BuiltIn { get; } = BuildBuiltIn();

    // A fresh catalog = the built-in set with hiddenDefaults removed, then the user
    // emotes layered on top (same-named user emotes win, and re-add a hidden default).
    public static EmoteCatalog WithUser(IEnumerable<Emote> userEmotes, IEnumerable<string> hiddenDefaults)
    {
        EmoteCatalog c = BuildBuiltIn();
        foreach (string h in hiddenDefaults) c._shortcodes.Remove(h);
        foreach (Emote e in userEmotes) c.Upsert(e);
        return c;
    }

    private static EmoteCatalog BuildBuiltIn()
    {
        EmoteCatalog c = new();

        // ----- Classic emoticons (case-sensitive literals) -----------------
        c.AddEmoticon(":)",  "🙂");
        c.AddEmoticon(":-)", "🙂");
        c.AddEmoticon(":(",  "🙁");
        c.AddEmoticon(":-(", "🙁");
        c.AddEmoticon(":D",  "😀");
        c.AddEmoticon(":-D", "😀");
        c.AddEmoticon(";)",  "😉");
        c.AddEmoticon(";-)", "😉");
        c.AddEmoticon(":P",  "😛");
        c.AddEmoticon(":-P", "😛");
        c.AddEmoticon(":p",  "😛");
        c.AddEmoticon(":'(", "😢");
        c.AddEmoticon(":o",  "😮");
        c.AddEmoticon(":O",  "😮");
        c.AddEmoticon(":|",  "😐");
        c.AddEmoticon(":*",  "😘");
        c.AddEmoticon("xD",  "😆");
        c.AddEmoticon("XD",  "😆");
        c.AddEmoticon(">:(", "😠");
        c.AddEmoticon("8)",  "😎");
        c.AddEmoticon("<3",  "❤️");
        c.AddEmoticon("</3", "💔");

        // ----- Word shortcodes (Unicode) -----------------------------------
        c.AddWord("smile", "🙂");
        c.AddWord("grin", "😁");
        c.AddWord("joy", "😂");
        c.AddWord("lol", "😂");
        c.AddWord("rofl", "🤣");
        c.AddWord("laughing", "😆");
        c.AddWord("wink", "😉");
        c.AddWord("cry", "😢");
        c.AddWord("sob", "😭");
        c.AddWord("sad", "🙁");
        c.AddWord("frowning", "🙁");
        c.AddWord("heart", "❤️");
        c.AddWord("heart_eyes", "😍");
        c.AddWord("kiss", "😘");
        c.AddWord("thumbsup", "👍");
        c.AddWord("+1", "👍");
        c.AddWord("thumbsdown", "👎");
        c.AddWord("-1", "👎");
        c.AddWord("ok_hand", "👌");
        c.AddWord("clap", "👏");
        c.AddWord("pray", "🙏");
        c.AddWord("wave", "👋");
        c.AddWord("muscle", "💪");
        c.AddWord("eyes", "👀");
        c.AddWord("thinking", "🤔");
        c.AddWord("shrug", "🤷");
        c.AddWord("facepalm", "🤦");
        c.AddWord("fire", "🔥");
        c.AddWord("100", "💯");
        c.AddWord("poop", "💩");
        c.AddWord("skull", "💀");
        c.AddWord("tada", "🎉");
        c.AddWord("rocket", "🚀");
        c.AddWord("star", "⭐");
        c.AddWord("sunglasses", "😎");
        c.AddWord("cool", "😎");
        c.AddWord("sweat", "😅");
        c.AddWord("angry", "😠");
        c.AddWord("rage", "😡");
        c.AddWord("sleeping", "😴");
        c.AddWord("vomit", "🤮");
        c.AddWord("clown", "🤡");
        c.AddWord("ghost", "👻");
        c.AddWord("alien", "👽");
        c.AddWord("robot", "🤖");
        c.AddWord("beer", "🍺");
        c.AddWord("coffee", "☕");
        c.AddWord("pizza", "🍕");
        c.AddWord("crown", "👑");
        c.AddWord("gg", "🎮");
        c.AddWord("wink2", "😜");

        // ----- Bundled Pepe image emotes -----------------------------------
        // Distributable set the user supplied; downscaled into Assets/Emotes.
        c.AddImage("monkas", "monkas");
        c.AddImage("pepecry", "pepecry");
        c.AddImage("sadge", "sadge");
        c.AddImage("sadge2", "sadge2");
        c.AddImage("pepejesus", "pepejesus");
        c.AddImage("copium", "copium");
        c.AddImage("pepeclown", "pepeclown");
        c.AddImage("pepeclown2", "pepeclown2");
        c.AddImage("pepehands", "pepehands");
        c.AddImage("sadgepray", "sadgepray");
        c.AddImage("peperage", "peperage");
        c.AddImage("pepeno", "pepeno");
        c.AddImage("pepeok", "pepeok");
        c.AddImage("pepethink", "pepethink");
        c.AddImage("pepedeath", "pepedeath");
        c.AddImage("prayge", "prayge");
        c.AddImage("pepecringe", "pepecringe");
        c.AddImage("pepehmm", "pepehmm");
        c.AddImage("clownge", "clownge");
        c.AddImage("poggies", "poggies");

        return c;
    }
}
