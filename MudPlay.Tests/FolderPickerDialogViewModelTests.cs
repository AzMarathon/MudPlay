using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

public sealed class FolderPickerDialogViewModelTests
{
    [Fact]
    public void Constructor_ListsRootFirstThenSortedFolders()
    {
        FolderPickerDialogViewModel vm = new(new[] { "Zeta", "alpha" });

        Assert.Equal(3, vm.Folders.Count);
        Assert.Equal(string.Empty, vm.Folders[0].Path);   // root first
        Assert.Equal("alpha", vm.Folders[1].Path);        // then alpha-sorted
        Assert.Equal("Zeta", vm.Folders[2].Path);
    }

    [Fact]
    public void Constructor_PreselectsCurrentFolder()
    {
        FolderPickerDialogViewModel vm = new(new[] { "alpha", "beta" }, current: "beta");
        Assert.Equal("beta", vm.Selected?.Path);
    }

    [Fact]
    public void Constructor_NoCurrent_PreselectsRoot()
    {
        FolderPickerDialogViewModel vm = new(new[] { "alpha" });
        Assert.Equal(string.Empty, vm.Selected?.Path);
    }

    [Fact]
    public void Save_ReturnsSelectedPath()
    {
        FolderPickerDialogViewModel vm = new(new[] { "alpha" }, current: "alpha");
        string? captured = "(unchanged)";
        vm.CloseRequested += p => captured = p;

        vm.SaveCommand.Execute(null);

        Assert.Equal("alpha", captured);
    }

    [Fact]
    public void Save_RootSelected_ReturnsEmptyStringNotNull()
    {
        // "" = move to root; null is reserved for Cancel.
        FolderPickerDialogViewModel vm = new(new[] { "alpha" });
        vm.Selected = vm.Folders[0];   // root
        string? captured = "(unchanged)";
        vm.CloseRequested += p => captured = p;

        vm.SaveCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(string.Empty, captured);
    }

    [Fact]
    public void Cancel_ReturnsNull()
    {
        FolderPickerDialogViewModel vm = new(new[] { "alpha" });
        string? captured = "(unchanged)";
        vm.CloseRequested += p => captured = p;

        vm.CancelCommand.Execute(null);

        Assert.Null(captured);
    }
}
