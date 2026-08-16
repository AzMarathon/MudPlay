using System.Collections.Generic;

namespace MudPlay.ViewModels.GameData.Edit;

// One player-typeable keyword a monster's greet textblock responds to, plus the
// indented effect lines that fire when it's asked. The Monster record surfaces
// the Keyword as a clickable chip on the Other Info tab; clicking it flies out
// the Effects. Grouping the decoded greet tree this way keeps a verbose block
// (many keywords) from blowing the pane out — the tab shows only the keywords,
// details are on demand.
public sealed record GreetKeyword(string Keyword, IReadOnlyList<string> Effects)
{
    public bool HasEffects => Effects is { Count: > 0 };
}
