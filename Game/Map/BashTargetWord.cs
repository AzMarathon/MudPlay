namespace MudPlay.Game.Map;

// Whether the word after `bash` could name someone in the room. At an exit that is
// no door, `bash <dir>` is the bash attack, aimed at whoever the word names, and the
// game takes the word for a name when it starts the name or any later word of it
// (GAME_MECHANICS "Locked doors — picking, opening and bashing", the bash attack's
// target match). So `bash w` there swings at a "large dire wolf".
public static class BashTargetWord
{
    public static bool Names(string word, string shownName)
    {
        if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(shownName)) return false;
        foreach (string part in shownName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
