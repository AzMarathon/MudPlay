using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

// A Workshop tab that holds related sections as sub-tabs (Auto-Train, My Equipment,
// Record Keeping), so the top strip stays short enough to read. Each sub-tab keeps
// the id it had as a tab of its own: menu entries and deep links saved against
// "bosses" or "equipment" still land on it.
public sealed partial class WorkshopGroupSectionViewModel : WorkshopSectionViewModel
{
    private Control? _view;

    public WorkshopGroupSectionViewModel(string id, string title, params WorkshopSectionViewModel[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Length == 0) throw new ArgumentException("A group needs at least one sub-tab.", nameof(children));
        Id = id;
        Title = title;
        Children = children;
        _selectedChild = children[0];
        foreach (WorkshopSectionViewModel child in children)
            child.LayoutChanged += OnChildLayoutChanged;
    }

    public override string Id { get; }
    public override string Title { get; }
    public IReadOnlyList<WorkshopSectionViewModel> Children { get; }

    // Null only for the instant the tab strip swaps items; Shown covers it.
    [ObservableProperty] private WorkshopSectionViewModel? _selectedChild;
    private WorkshopSectionViewModel Shown => SelectedChild ?? Children[0];

    public override Control View => _view ??= new WorkshopGroupView { DataContext = this };
    public override WorkshopSectionViewModel Leaf => Shown.Leaf;
    public override Size? PreferredSize => Shown.PreferredSize;

    partial void OnSelectedChildChanged(WorkshopSectionViewModel? value)
    {
        if (value is not null) RaiseLayoutChanged();
    }

    private void OnChildLayoutChanged(WorkshopSectionViewModel child)
    {
        if (ReferenceEquals(child, Shown)) RaiseLayoutChanged();
    }

    public override void Dispose()
    {
        foreach (WorkshopSectionViewModel child in Children)
        {
            child.LayoutChanged -= OnChildLayoutChanged;
            child.Dispose();
        }
    }
}
