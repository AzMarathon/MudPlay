namespace MudPlay.Game;

// The client's own terminal notices ("[CONNECTING TO: …]", "[… Quest is Now
// Available]", "[Round 3 dealt: …]") are fed back through the emulator, so they reach
// every line observer like server output. A full-line "[ … ]" is never a server
// message; an observer that reads meaning into a line's wording or colour skips it.
public static class ClientNotice
{
    public static bool IsNotice(string text) =>
        text.Length >= 2 && text[0] == '[' && text[^1] == ']';
}
