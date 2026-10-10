using System;
using System.Text;

namespace MudPlay.Game.Inventory;

// Watches the outbound line stream for the two commands the Chest Offload list has
// to know went out: `open <target>`, so a chest opened from the terminal is tracked
// the same as one opened from the list's own button, and an inventory read, so the
// tracker can tell the read it asked for after an open from one asked for before it.
// It sees every send, not only typed ones: the engines' wire senders run through the
// same MainWindowViewModel.SendUserInput. So the listener has to tell its own opens
// apart (ChestOpenTracker does, while it sends one) and decide whether the target is
// a carried container at all (a door engine's `open n` is not).
//
// The command word is matched the way the game does: only the abbreviations it
// takes (GAME_MECHANICS "Command words and abbreviations").
public sealed class OutboundOpenObserver
{
    public event Action<string>? OpenSent;

    // An inventory read went out (`i`, or `inve` … `inventory`).
    public event Action? InventoryRequested;

    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        // A command, not a paste.
        if (bytes.Length is 0 or > 128) return;
        string text = Encoding.Latin1.GetString(bytes);
        foreach (string raw in text.Split('\r', '\n'))
        {
            string line = raw.Trim();
            int space = line.IndexOf(' ');
            string word = space < 0 ? line : line[..space];
            if (IsOpen(word))
            {
                string target = space < 0 ? "" : line[(space + 1)..].Trim();
                if (target.Length > 0) OpenSent?.Invoke(target);
            }
            // Only the first word is looked up, so words after it change nothing.
            else if (IsInventory(word)) InventoryRequested?.Invoke();
        }
    }

    // `op`, `ope`, `open`.
    private static bool IsOpen(string word) =>
        word.Length >= 2 && "open".StartsWith(word, StringComparison.OrdinalIgnoreCase);

    // `i` alone, or `inve` and longer; `in` and `inv` are nothing to the game.
    private static bool IsInventory(string word) =>
        word.Equals("i", StringComparison.OrdinalIgnoreCase)
        || (word.Length >= 4 && "inventory".StartsWith(word, StringComparison.OrdinalIgnoreCase));
}
