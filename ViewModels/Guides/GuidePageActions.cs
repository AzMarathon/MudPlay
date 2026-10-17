using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.ViewModels.Guides;

// Which GuideActions a page coming on screen reports, for the windows that show
// pages by id (the Game Data Browser, the Player Workshop).
public static class GuidePageActions
{
    // A Game Data Browser section change. The section the browser opens on still
    // names itself (View → Macros ticks "open Macros"), but only a later change is
    // the user picking a table.
    public static IEnumerable<string> ForBrowserSection(string? oldId, string newId)
    {
        if (oldId is not null) yield return GuideActions.BrowserSectionPicked;
        string? shown = newId switch
        {
            "macros" => GuideActions.MacrosShown,
            "aliases" => GuideActions.AliasesShown,
            "triggers" => GuideActions.TriggersShown,
            "players" => GuideActions.PlayersShown,
            _ => null,
        };
        if (shown is not null) yield return shown;
    }

    // A Player Workshop page on screen, or null when no guide waits for it.
    public static string? ForWorkshopPage(string pageId) => pageId switch
    {
        ItemFinderViewModel.SectionId => GuideActions.ItemFinderShown,
        EquipmentSectionViewModel.SectionId => GuideActions.EquipmentManagerShown,
        _ => null,
    };
}
