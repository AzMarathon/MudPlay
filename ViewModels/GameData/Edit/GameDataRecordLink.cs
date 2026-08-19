using System.Windows.Input;

namespace MudPlay.ViewModels.GameData.Edit;

// One clickable record reference inside a GameDataInfoRow's value — a blue MdbLink
// that opens the target's Game Data record (monster / spell / item). Trailing
// carries the ", " separator for every link but the last, so an inline list of
// them renders "a, b, c" with only the names clickable. Distinct from the
// Models.GameData.GameDataLink data struct (a Table+Number message-link edit row);
// this one carries the resolved name and the open command for display.
public sealed class GameDataRecordLink
{
    public string Name { get; }
    public string Trailing { get; }
    public ICommand Open { get; }

    // Clickable (a real Monsters / Items / Spells record) vs inert text (an
    // unlinkable source kind like Room / Class, or the "+ more" cap marker). The
    // template renders a blue link only when linked, plain text otherwise, so
    // nothing looks clickable that isn't.
    public bool IsLinked { get; }

    public GameDataRecordLink(string name, string trailing, ICommand open, bool isLinked = true)
    {
        Name = name;
        Trailing = trailing;
        Open = open;
        IsLinked = isLinked;
    }
}
