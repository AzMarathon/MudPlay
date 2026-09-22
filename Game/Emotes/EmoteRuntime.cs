using System;

namespace MudPlay.Game.Emotes;

// The live emote scanner the conversation renderer reads. Starts as the built-in set
// so emotes work before any service wires up; EmoteStore publishes a merged
// built-in+user scanner and raises Changed whenever the user's emote set changes, so
// open conversation windows re-render against the new catalog.
public static class EmoteRuntime
{
    private static EmoteScanner _scanner = EmoteScanner.BuiltIn;

    public static EmoteScanner Scanner => _scanner;

    public static event Action? Changed;

    public static void Publish(EmoteScanner scanner)
    {
        _scanner = scanner ?? EmoteScanner.BuiltIn;
        Changed?.Invoke();
    }
}
