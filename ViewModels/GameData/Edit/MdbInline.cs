using System.Windows.Input;

namespace MudPlay.ViewModels.GameData.Edit;

// One run of a mixed value line in a monster record. A plain run (Open == null)
// is text; a link run carries a command that opens a related record as a cyan
// MdbLink chip. This lets a row read as running text with a single embedded link
// — e.g. Between Rounds "(25%) [<spell link>, lvl 25]" (the summon spell links to
// its Spell record) and Summons "<monster link> (between rounds, 25%)" (the
// summoned NPC links to its Monster record) — which the flat Rooms / Items chip
// lists (all-chips-or-nothing) can't express.
public sealed class MdbInline
{
    public string Text { get; }
    public ICommand? Open { get; }
    public bool IsLink => Open is not null;
    public bool IsPlainRun => Open is null;

    public MdbInline(string text, ICommand? open = null)
    {
        Text = text;
        Open = open;
    }
}
