using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// Shared opener for the interactive shop / room detail popup, so every place that
// opens it gets the same dialog. The dialog reuses RoomTooltipBuilder for the
// descriptive text (so it never drifts from the Navigation map hover) and layers
// clickable room/exit/monster links on top.
public static class RoomDetailPopup
{
    public static void Show(DialogService dialogs, RoomKey key)
    {
        RoomDetailDialogViewModel vm = new(AppServices.Current, key);
        // Modeless, fire-and-forget: the popup centres the map via AppServices,
        // so nothing awaits a result — the user dismisses it with Close / the
        // title-bar X.
        _ = dialogs.OpenWindowAsync<RoomDetailDialogViewModel, bool>(vm);
    }
}
