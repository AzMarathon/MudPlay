using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using MudPlay.Models.Settings;
using MudPlay.Terminal;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// The terminal-colour picker in Settings → General: five choices, the 16 colours of
// the one picked, and the user's own colours kept across a switch away and back.
public sealed class TerminalColorsEditorViewModelTests
{
    private int _changes;
    private TerminalColorsEditorViewModel New(TerminalColorSettings? saved = null)
    {
        var vm = new TerminalColorsEditorViewModel(() => _changes++);
        vm.Load(saved);
        _changes = 0;
        return vm;
    }

    private static uint[] Shown(TerminalColorsEditorViewModel vm) =>
        vm.NormalRows.Concat(vm.BrightRows).Select(r => r.Argb).ToArray();

    [Fact]
    public void NothingSaved_IsTheStandardColours_AndSavesNothing()
    {
        TerminalColorsEditorViewModel vm = New();

        Assert.True(vm.IsStandard);
        Assert.Equal(AnsiPalette.Default16, Shown(vm));
        Assert.All(vm.NormalRows.Concat(vm.BrightRows), r => Assert.False(r.CanEdit));
        Assert.Null(vm.ToSettings());
        Assert.Equal(0, _changes);        // loading is not a change
    }

    [Fact]
    public void PickingAMadeScheme_ShowsItsColours_AndSavesItsName()
    {
        TerminalColorsEditorViewModel vm = New();

        vm.IsStandard = false;
        vm.IsDeuteranopia = true;

        Assert.Equal(AnsiColorSchemes.For(AnsiColorScheme.Deuteranopia), Shown(vm));
        Assert.Equal("Deuteranopia", vm.ToSettings()!.Scheme);
        Assert.Null(vm.ToSettings()!.Colors);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void Custom_StartsFromTheSchemeOnShow_AndItsRowsCanBeEdited()
    {
        TerminalColorsEditorViewModel vm = New(new TerminalColorSettings { Scheme = "Protanopia" });

        vm.IsProtanopia = false;
        vm.IsCustom = true;

        Assert.Equal(AnsiColorSchemes.For(AnsiColorScheme.Protanopia), Shown(vm));
        Assert.All(vm.NormalRows.Concat(vm.BrightRows), r => Assert.True(r.CanEdit));

        vm.NormalRows[1].Color = Color.FromRgb(0xD5, 0x5E, 0x00);

        TerminalColorSettings saved = vm.ToSettings()!;
        Assert.Equal("Custom", saved.Scheme);
        Assert.Equal("#D55E00", saved.Colors![1]);
    }

    [Fact]
    public void TheUsersColours_AreKeptWhileAnotherSchemeIsPicked()
    {
        TerminalColorsEditorViewModel vm = New(new TerminalColorSettings
        {
            Scheme = "Custom", Colors = new Dictionary<int, string> { [4] = "#0072B2" },
        });
        Assert.Equal(0xFF0072B2u, vm.NormalRows[4].Argb);

        vm.IsCustom = false;
        vm.IsTritanopia = true;
        Assert.Equal(AnsiColorSchemes.For(AnsiColorScheme.Tritanopia), Shown(vm));
        // Saved under the scheme now picked, with the user's own colour still kept.
        Assert.Equal("Tritanopia", vm.ToSettings()!.Scheme);
        Assert.Equal("#0072B2", vm.ToSettings()!.Colors![4]);

        vm.IsTritanopia = false;
        vm.IsCustom = true;
        Assert.Equal(0xFF0072B2u, vm.NormalRows[4].Argb);
        Assert.Equal(AnsiPalette.Default16[1], vm.NormalRows[1].Argb);   // not re-seeded from Tritanopia
    }

    [Fact]
    public void Reset_PutsOneColourBack_AndRestore_PutsThemAllBack()
    {
        TerminalColorsEditorViewModel vm = New(new TerminalColorSettings
        {
            Scheme = "Custom", Colors = new Dictionary<int, string> { [2] = "#009E73", [9] = "#CC79A7" },
        });

        vm.NormalRows[2].ResetCommand.Execute(null);
        Assert.Equal(AnsiPalette.Default16[2], vm.NormalRows[2].Argb);
        Assert.Equal(new[] { 9 }, vm.ToSettings()!.Colors!.Keys);

        vm.RestoreStandardColorsCommand.Execute(null);
        Assert.Equal(AnsiPalette.Default16, Shown(vm));
        Assert.Equal("Custom", vm.ToSettings()!.Scheme);
        Assert.Null(vm.ToSettings()!.Colors);
    }

    [Fact]
    public void ShowingAScheme_IsNotTakenForAnEdit()
    {
        // Switching schemes repaints every row; that must not write the made
        // scheme's colours into the user's own.
        TerminalColorsEditorViewModel vm = New(new TerminalColorSettings
        {
            Scheme = "Custom", Colors = new Dictionary<int, string> { [1] = "#D55E00" },
        });

        vm.IsCustom = false;
        vm.IsDeuteranopia = true;
        vm.IsDeuteranopia = false;
        vm.IsStandard = true;

        Assert.Equal(new Dictionary<int, string> { [1] = "#D55E00" }, vm.ToSettings()!.Colors);
        Assert.Null(vm.ToSettings()!.Scheme);
    }
}
