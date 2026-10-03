using Avalonia.Controls;

namespace MudPlay.ViewModels.CharacterWorkshop;

// A Workshop tab whose section is built the first time its content is shown. The
// Workshop is rebuilt on every open, and building every section up front was most
// of the open's half second; the tab strip only asks for the selected tab's View,
// so a tab never visited costs nothing. Only sections that just read shared state
// can wait: Quest Status publishes the quest bonuses other tabs fold in, so it is
// built with the window.
public sealed class LazyWorkshopSection : WorkshopSectionViewModel
{
    private readonly Func<WorkshopSectionViewModel> _create;
    private WorkshopSectionViewModel? _section;

    public LazyWorkshopSection(string id, string title, Func<WorkshopSectionViewModel> create)
    {
        Id = id;
        Title = title;
        _create = create ?? throw new ArgumentNullException(nameof(create));
    }

    public override string Id { get; }
    public override string Title { get; }

    // The real section, built on first use.
    public WorkshopSectionViewModel Section => _section ??= _create();

    public override Control View => Section.View;

    public override void Dispose() => _section?.Dispose();
}
