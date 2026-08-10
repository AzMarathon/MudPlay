using MudPlay.Game.Map;

namespace MudPlay.ViewModels.Navigation;

// One row in the Navigation goto-button history dropdown: a recent walk-to
// destination, its resolved display label, and the RoomKey the select command arms.
public sealed class GotoHistoryRowViewModel
{
    public RoomKey Key { get; }
    public string Label { get; }

    public GotoHistoryRowViewModel(RoomKey key, string label)
    {
        Key = key;
        Label = label;
    }
}
