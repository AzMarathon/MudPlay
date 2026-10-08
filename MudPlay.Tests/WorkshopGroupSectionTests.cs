using System;
using Avalonia;
using Avalonia.Controls;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// A grouped Workshop tab stands in for whichever sub-tab it is showing: the window
// sizes itself by that sub-tab, and deep links address sub-tabs by the ids they had
// as tabs of their own.
public sealed class WorkshopGroupSectionTests
{
    private sealed class Section(string id, Size? size = null) : WorkshopSectionViewModel
    {
        public Size? Size { get; set; } = size;
        public bool Disposed { get; private set; }
        public override string Id => id;
        public override string Title => id;
        public override Control View => throw new NotSupportedException("no view in a test");
        public override Size? PreferredSize => Size;
        public void Resize(Size? to) { Size = to; RaiseLayoutChanged(); }
        public override void Dispose() => Disposed = true;
    }

    [Fact]
    public void Group_IsItsSelectedSubTab_ForLeafAndSize()
    {
        var form = new Section("form");
        var grid = new Section("grid", new Size(1180, 780));
        var group = new WorkshopGroupSectionViewModel("group", "Group", form, grid);

        Assert.Same(form, group.Leaf);
        Assert.Null(group.PreferredSize);

        group.SelectedChild = grid;
        Assert.Same(grid, group.Leaf);
        Assert.Equal(new Size(1180, 780), group.PreferredSize);
    }

    [Fact]
    public void Group_ReportsASubTabSwitch_AndOnlyTheShownSubTabsResize()
    {
        var form = new Section("form");
        var grid = new Section("grid", new Size(1180, 780));
        var group = new WorkshopGroupSectionViewModel("group", "Group", form, grid);
        int changes = 0;
        group.LayoutChanged += _ => changes++;

        grid.Resize(new Size(1532, 780));
        Assert.Equal(0, changes);

        group.SelectedChild = grid;
        Assert.Equal(1, changes);

        grid.Resize(new Size(1180, 780));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void LazySubTab_IsBuiltOnlyWhenAsked_AndPassesItsResizeOn()
    {
        int built = 0;
        Section? real = null;
        var lazy = new LazyWorkshopSection("grid", "Grid", () =>
        {
            built++;
            return real = new Section("grid", new Size(1180, 780));
        });
        var group = new WorkshopGroupSectionViewModel("group", "Group", new Section("form"), lazy);
        int changes = 0;
        group.LayoutChanged += _ => changes++;

        group.SelectedChild = lazy;
        Assert.Equal(0, built);
        Assert.Same(lazy, group.Leaf);

        Assert.Equal(new Size(1180, 780), group.PreferredSize);
        Assert.Equal(1, built);

        real!.Resize(new Size(1532, 780));
        Assert.Equal(2, changes);

        group.Dispose();
        Assert.True(real.Disposed);
    }
}
