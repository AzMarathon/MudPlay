namespace MudPlay.Game.Inventory;

// Emits an item command (get / drop / hide / sell / buy) that may be batched.
// Paradigm accepts a counted action — one "<verb> <count> <item>" line acts on N
// copies at once and echoes the count-prefixed SINGULAR name back
// ("You hid 35 orc-head."). Stock has no batching, so a count collapses to one
// "<verb> <item>" line per copy. count <= 0 sends nothing; count == 1 is a single
// line either way. The name is the singular send name — the same one the reply
// parsers strip a leading count from (see InventoryManager.SplitLeadingCount).
public static class CountedCommand
{
    public static void Emit(Action<string> send, string verb, int count, string name, bool paradigm)
    {
        if (count <= 0) return;
        if (paradigm && count > 1)
        {
            send($"{verb} {count} {name}");
            return;
        }
        for (int i = 0; i < count; i++) send($"{verb} {name}");
    }

    // Inverse of Emit for reply parsing: split a counted item token ("35 orc-head")
    // into its leading digit count and the bare SINGULAR item name. A token with no
    // leading digit count ("a rusty dagger" — Stock's single form, or any lone
    // item) is (1, token). Paradigm keeps the item name singular under a count, so
    // the stripped name matches the carried list and the item resolver directly.
    public static (int Count, string Name) SplitLeadingCount(string token)
    {
        int sp = token.IndexOf(' ');
        if (sp > 0 && int.TryParse(token.AsSpan(0, sp), out int n) && n > 0)
            return (n, token[(sp + 1)..].TrimStart());
        return (1, token);
    }
}
