using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using MudPlay.ViewModels.StatusBar;
using Xunit;

namespace MudPlay.Tests;

// The configurable status bar: its saved layout, the item catalogue, custom-text
// expansion, the marquee crawl and the Settings editor. Drawing the bar needs the
// main window and isn't exercised here.
public sealed class StatusBarTests
{
    // ----- Layout (global settings) --------------------------------------------

    [Fact]
    public void DefaultLayout_IsTheOriginalBar()
    {
        StatusBarRow row = Assert.Single(new StatusBarSettings().Rows);
        Assert.Equal(new[] { "engine", "location", "exprate", "tnl" }, row.Left.Select(e => e.Item));
        Assert.Equal(new[] { "target" }, row.Center.Select(e => e.Item));
        Assert.Equal(new[] { "statline", "tick", "hptick", "matick", "connection" }, row.Right.Select(e => e.Item));
        Assert.False(row.Marquee);
    }

    [Fact]
    public void DefaultLayout_IsNotStored()
    {
        GlobalSettings global = new() { Settings = new Dictionary<string, JsonElement>() };
        new StatusBarSettings().WriteTo(global);
        Assert.False(global.Settings.ContainsKey(StatusBarSettings.SectionKey));
        Assert.True(StatusBarSettings.Read(global).IsDefault());
    }

    [Fact]
    public void ChangedLayout_RoundTripsThroughGlobalSettings_AndResetDropsIt()
    {
        StatusBarSettings layout = new();
        layout.Rows.Add(new StatusBarRow
        {
            Left = { new StatusBarEntry("profile"), new StatusBarEntry("text", "Lap {lap} of {loop}") },
            Marquee = true,
        });
        GlobalSettings global = new();
        layout.WriteTo(global);

        // Through JSON, as the settings file would carry it.
        GlobalSettings reloaded = JsonSerializer.Deserialize<GlobalSettings>(JsonSerializer.Serialize(global))!;
        StatusBarSettings back = StatusBarSettings.Read(reloaded);

        Assert.Equal(2, back.Rows.Count);
        Assert.True(back.Rows[1].Marquee);
        Assert.Equal("Lap {lap} of {loop}", back.Rows[1].Left[1].Text);
        Assert.False(back.IsDefault());

        new StatusBarSettings().WriteTo(reloaded);
        Assert.False(reloaded.Settings!.ContainsKey(StatusBarSettings.SectionKey));
    }

    [Theory]
    [InlineData("""{ "Rows": [] }""")]
    [InlineData("""{ "Rows": "oops" }""")]
    [InlineData("\"not an object\"")]
    public void UnusableSavedLayout_FallsBackToTheDefault(string saved)
    {
        GlobalSettings global = new()
        {
            Settings = new Dictionary<string, JsonElement>
            {
                [StatusBarSettings.SectionKey] = JsonDocument.Parse(saved).RootElement.Clone(),
            },
        };
        Assert.True(StatusBarSettings.Read(global).IsDefault());
    }

    // ----- Catalogue -----------------------------------------------------------

    [Fact]
    public void Catalogue_IdsAreUnique_LowerCase_AndCoverTheDefaultLayout()
    {
        IReadOnlyList<StatusBarItemDef> all = StatusBarItemCatalogue.All;
        Assert.Equal(all.Count, all.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, d => Assert.Matches("^[a-z]+$", d.Id));
        Assert.All(all, d => Assert.False(string.IsNullOrWhiteSpace(d.Sample)));

        StatusBarRow row = StatusBarRow.Default();
        Assert.All(row.Left.Concat(row.Center).Concat(row.Right),
            e => Assert.NotNull(StatusBarItemCatalogue.Find(e.Item)));
    }

    [Fact]
    public void Catalogue_Groups_KeepEveryItem_InListOrder()
    {
        Assert.Equal(StatusBarItemCatalogue.All, StatusBarItemCatalogue.Groups.SelectMany(g => g));
        Assert.Equal("Standard bar", StatusBarItemCatalogue.Groups[0].Key);
        Assert.Contains("{combatprofilename}", StatusBarItemCatalogue.TokenNames);
        Assert.DoesNotContain("{text}", StatusBarItemCatalogue.TokenNames);
    }

    [Theory]
    [InlineData("Lap {lap} of {loop}", "Lap <lap> of <loop>")]
    [InlineData("{HP} now", "<hp> now")]                      // names match in any case
    [InlineData("{nosuchitem} stays", "{nosuchitem} stays")]  // a typo shows as typed
    [InlineData("{text} can't nest", "{text} can't nest")]    // custom text isn't a value
    [InlineData("unclosed {lap", "unclosed {lap")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void CustomText_ReplacesItemNamesInBraces(string template, string expected) =>
        Assert.Equal(expected, StatusBarItemCatalogue.ExpandTokens(template, id => $"<{id}>"));

    // ----- Marquee -------------------------------------------------------------

    [Fact]
    public void Marquee_SlidesLeftOneCharacterPerFrame_AndRepeats()
    {
        string unit = "ABC" + StatusBarMarquee.Gap;
        Assert.StartsWith("ABC" + StatusBarMarquee.Gap + "ABC", StatusBarMarquee.Frame("ABC", 0));
        Assert.StartsWith("BC" + StatusBarMarquee.Gap + "ABC", StatusBarMarquee.Frame("ABC", 1));
        Assert.Equal(StatusBarMarquee.Frame("ABC", 0), StatusBarMarquee.Frame("ABC", unit.Length));
        Assert.Equal(StatusBarMarquee.Frame("ABC", unit.Length - 1), StatusBarMarquee.Frame("ABC", -1));
    }

    [Fact]
    public void Marquee_NothingToShow_IsEmpty()
    {
        Assert.Equal(string.Empty, StatusBarMarquee.Frame("", 5));
        Assert.Equal(string.Empty, StatusBarMarquee.Frame("   ", 5));
    }

    // ----- Editor --------------------------------------------------------------

    private static StatusBarEditorViewModel Editor(out List<int> changes)
    {
        StatusBarEditorViewModel editor = new(() => null);
        editor.Load(new StatusBarSettings());
        List<int> seen = new();
        editor.Changed += () => seen.Add(seen.Count);
        changes = seen;
        return editor;
    }

    [Fact]
    public void Editor_LoadedDefault_SavesBackAsDefault_WithoutReportingAChange()
    {
        StatusBarEditorViewModel editor = Editor(out List<int> changes);
        Assert.True(editor.ToSettings().IsDefault());
        Assert.Empty(changes);
        Assert.False(editor.Rows[0].CanRemove);
    }

    [Fact]
    public void Editor_AddMoveRemove_ReshapesAZone()
    {
        StatusBarEditorViewModel editor = Editor(out List<int> changes);
        StatusBarZoneEditor centre = editor.Rows[0].Center;

        centre.Add(StatusBarItemCatalogue.Find("profile")!);
        centre.Add(StatusBarItemCatalogue.Find("hp")!);
        Assert.Equal(new[] { "target", "profile", "hp" }, centre.Entries.Select(e => e.Def.Id));

        centre.Move(centre.Entries[2], -1);
        Assert.False(centre.CanMove(centre.Entries[0], -1));
        centre.Move(centre.Entries[0], -1);   // already first: no move, no change
        Assert.Equal(new[] { "target", "hp", "profile" }, centre.Entries.Select(e => e.Def.Id));

        centre.Remove(centre.Entries[0]);
        Assert.Equal(new[] { "hp", "profile" }, editor.ToSettings().Rows[0].Center.Select(e => e.Item));
        Assert.Equal(4, changes.Count);
    }

    [Fact]
    public void Editor_MoveTo_CarriesAnItemToAnotherSideOrRow_AsOneChange()
    {
        StatusBarEditorViewModel editor = Editor(out List<int> changes);
        editor.AddRowCommand.Execute(null);
        StatusBarRowEditor first = editor.Rows[0], second = editor.Rows[1];
        first.Center.Add(StatusBarItemCatalogue.Find(StatusBarItemCatalogue.CustomTextId)!, "Lap {lap}");
        int before = changes.Count;

        StatusBarEntryEditor text = first.Center.Entries[^1];
        second.Right.MoveHere(text);

        Assert.DoesNotContain(text, first.Center.Entries);
        StatusBarEntry moved = Assert.Single(editor.ToSettings().Rows[1].Right);
        Assert.Equal(("text", "Lap {lap}"), (moved.Item, moved.Text));
        Assert.Equal(before + 1, changes.Count);

        second.Right.MoveHere(second.Right.Entries[0]);   // onto its own side: nothing happens
        Assert.Equal(before + 1, changes.Count);
    }

    [Fact]
    public void Editor_CustomText_IsSavedWithItsText()
    {
        StatusBarEditorViewModel editor = Editor(out List<int> changes);
        StatusBarZoneEditor centre = editor.Rows[0].Center;
        centre.Add(StatusBarItemCatalogue.Find(StatusBarItemCatalogue.CustomTextId)!);
        centre.Entries[^1].Text = "Lap {lap}";

        StatusBarEntry saved = editor.ToSettings().Rows[0].Center[^1];
        Assert.Equal(("text", "Lap {lap}"), (saved.Item, saved.Text));
        Assert.Equal(2, changes.Count);   // the add, then the typing
    }

    [Fact]
    public void Editor_Rows_AddUpToTheLimit_KeepAtLeastOne_AndRenumber()
    {
        StatusBarEditorViewModel editor = Editor(out _);
        while (editor.AddRowCommand.CanExecute(null)) editor.AddRowCommand.Execute(null);
        Assert.Equal(StatusBarSettings.MaxRows, editor.Rows.Count);

        editor.Rows[1].Marquee = true;
        editor.Rows[0].RemoveCommand.Execute(null);
        Assert.Equal("Status bar 1", editor.Rows[0].Title);
        Assert.True(editor.ToSettings().Rows[0].Marquee);

        while (editor.Rows.Count > 1) editor.Rows[^1].RemoveCommand.Execute(null);
        editor.Rows[0].RemoveCommand.Execute(null);   // the last row stays
        Assert.Single(editor.Rows);
        Assert.False(editor.Rows[0].CanRemove);
    }

    [Fact]
    public void Editor_Reset_ReturnsToTheDefault_AndReportsTheChange()
    {
        StatusBarEditorViewModel editor = Editor(out List<int> changes);
        editor.AddRowCommand.Execute(null);
        editor.ResetCommand.Execute(null);
        Assert.True(editor.ToSettings().IsDefault());
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void Editor_LayoutNamingAnUnknownItem_DropsOnlyThatItem()
    {
        StatusBarSettings saved = new();
        saved.Rows[0].Left.Insert(1, new StatusBarEntry("from-a-newer-build"));
        StatusBarEditorViewModel editor = new(() => null);
        editor.Load(saved);
        Assert.Equal(new[] { "engine", "location", "exprate", "tnl" }, editor.Rows[0].Left.Entries.Select(e => e.Def.Id));
    }
}
