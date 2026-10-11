using System.Collections.Generic;

namespace MudPlay.Game.Map;

// What a re-read of one catalogue's files found, by name.
internal sealed record NavReread<T>(
    Dictionary<string, T> Fresh, List<string> Added, List<string> Changed, List<string> Removed)
    where T : class
{
    public bool AnyItemChanged => Added.Count + Changed.Count + Removed.Count > 0;
}
