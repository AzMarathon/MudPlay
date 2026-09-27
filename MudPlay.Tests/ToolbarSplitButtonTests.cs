using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A split toolbar button (Drop All / Hide All / Equip) runs its ▾ pick: the first option
// by default or the character's saved one, and picking from the menu both sets it and
// runs it.
public sealed class ToolbarSplitButtonTests
{
    private static (ToolbarButtonItem Item, List<string> Ran, List<string> Saved) Build(string? savedChoice)
    {
        List<string> ran = new();
        List<string> saved = new();
        RelayCommand<string?> cmd = new(p => ran.Add(p ?? "?"));
        List<ToolbarMenuAction> subs = new()
        {
            new("Unworn", cmd, null, "Drop:unworn", "unworn"),
            new("Everything", cmd, null, "Drop:everything", "everything"),
            new("Coins", cmd, null, "Drop:coins", "coins"),
        };
        ToolbarButtonItem item = new(ToolbarItemKind.Button, "ActionDropAll", "Drop All", null, "Drop All",
            command: null, subActions: subs, savedChoice: savedChoice, saveChoice: saved.Add);
        return (item, ran, saved);
    }

    [Fact]
    public void Click_RunsTheFirstOption_ByDefault()
    {
        (ToolbarButtonItem item, List<string> ran, _) = Build(null);
        item.Command!.Execute(null);
        Assert.Equal(new[] { "unworn" }, ran);
    }

    [Fact]
    public void SavedChoice_IsWhatAClickRuns()
    {
        (ToolbarButtonItem item, List<string> ran, _) = Build("Drop:coins");
        item.Command!.Execute(null);
        Assert.Equal(new[] { "coins" }, ran);
        Assert.Equal("Drop All: Coins — ▾ to change", item.Tooltip);
    }

    [Fact]
    public void Choose_SetsAndSavesWithoutRunning_ThenClicksFollowIt()
    {
        (ToolbarButtonItem item, List<string> ran, List<string> saved) = Build(null);

        item.Choose(item.SubActions[1]);
        Assert.Empty(ran);
        item.Command!.Execute(null);

        Assert.Equal(new[] { "everything" }, ran);
        Assert.Equal(new[] { "Drop:everything" }, saved);
        Assert.Same(item.SubActions[1], item.SelectedSubAction);
    }

    [Fact]
    public void UnknownSavedChoice_FallsBackToTheFirst()
    {
        (ToolbarButtonItem item, _, _) = Build("Drop:gone");
        Assert.Same(item.SubActions[0], item.SelectedSubAction);
    }
}
