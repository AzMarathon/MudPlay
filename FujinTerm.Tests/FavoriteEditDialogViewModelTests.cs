using FujinTerm.ViewModels.Navigation;
using Xunit;

namespace FujinTerm.Tests;

public sealed class FavoriteEditDialogViewModelTests
{
    private static FavoriteEditDialogViewModel Make(string label = "Bank", int map = 1, int room = 45)
        => new(label, map, room, (m, r) => $"Room {m}/{r}");

    [Fact]
    public void Save_ReturnsTrimmedLabelAndCoordinate()
    {
        FavoriteEditDialogViewModel vm = Make("  Bank of Godfrey  ", 2, 297);
        FavoriteEditResult? captured = null;
        bool fired = false;
        vm.CloseRequested += r => { captured = r; fired = true; };

        vm.SaveCommand.Execute(null);

        Assert.True(fired);
        Assert.NotNull(captured);
        Assert.Equal("Bank of Godfrey", captured!.Label);
        Assert.Equal(2, captured.Map);
        Assert.Equal(297, captured.Room);
    }

    [Fact]
    public void Save_BlankLabel_ReturnsNullLabel()
    {
        // Blank label falls back to the graph room name — null is that signal.
        FavoriteEditDialogViewModel vm = Make();
        vm.Label = "   ";
        FavoriteEditResult? captured = null;
        vm.CloseRequested += r => captured = r;

        vm.SaveCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Null(captured!.Label);
    }

    [Fact]
    public void Cancel_ReturnsNull()
    {
        FavoriteEditDialogViewModel vm = Make();
        FavoriteEditResult? captured = new(null, 0, 0);
        bool fired = false;
        vm.CloseRequested += r => { captured = r; fired = true; };

        vm.CancelCommand.Execute(null);

        Assert.True(fired);
        Assert.Null(captured);
    }

    [Fact]
    public void RoomNamePreview_TracksEnteredCoordinate()
    {
        FavoriteEditDialogViewModel vm = Make("x", 1, 45);
        Assert.Equal("→ Room 1/45", vm.RoomNamePreview);

        vm.Map = 3;
        vm.Room = 9;
        Assert.Equal("→ Room 3/9", vm.RoomNamePreview);
    }
}
