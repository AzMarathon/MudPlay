using System.Collections.Generic;
using System.Linq;

namespace MudPlay.ViewModels.GameData.Edit;

// One player-typeable keyword a monster's greet textblock responds to, plus the
// indented effect lines that fire when it's asked. The Monster record surfaces
// the Keyword as a clickable chip on the Other Info tab; clicking it flies out
// the Effects. Grouping the decoded greet tree this way keeps a verbose block
// (many keywords) from blowing the pane out — the tab shows only the keywords,
// details are on demand. A keyword whose effects teleport says so on its chip —
// asking it moves you, which is the thing a player scanning the list wants to spot.
public sealed record GreetKeyword(string Keyword, IReadOnlyList<GreetEffect> Effects)
{
    public bool HasEffects => Effects is { Count: > 0 };
    public bool IsTeleport => Effects.Any(e => e.Room is not null);
    public string ChipLabel => IsTeleport ? $"{Keyword} (teleport)" : Keyword;
}

// One effect line under a greet keyword. A teleport line carries its destination as
// a RoomLink, so the flyout shows it as a link that opens the map on that room.
public sealed record GreetEffect(string Text, RoomLink? Room)
{
    public bool IsLink => Room is not null;
    public bool IsPlain => Room is null;
}
