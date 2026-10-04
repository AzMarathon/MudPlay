using System;
using System.Text;

namespace MudPlay.Game.Inventory;

// Watches what the user types for `open <target>` and announces the target, so the
// Chest Offload window can track a chest opened from the terminal the same as one
// opened from its own button. Only typed input reaches it (engine sends skip the
// outbound observers); the window decides whether the target is a carried container.
public sealed class OutboundOpenObserver
{
    public event Action<string>? OpenSent;

    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        // A command, not a paste.
        if (bytes.Length is 0 or > 128) return;
        string text = Encoding.Latin1.GetString(bytes);
        foreach (string raw in text.Split('\r', '\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("open ", StringComparison.OrdinalIgnoreCase)) continue;
            string target = line[5..].Trim();
            if (target.Length > 0) OpenSent?.Invoke(target);
        }
    }
}
