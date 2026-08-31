using System.Linq;
using System.Reflection;
using System.Windows.Input;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// The customizable terminal right-click menu resolves each catalogue entry's
// CommandName / ToggleProperty / GestureProperty against MainWindowViewModel by
// reflection at runtime — a typo there is a SILENT dead menu item, not a build
// error. These tests reflect over the type (no instance needed) so the whole
// catalogue's wiring is verified at test time.
public class MenuActionCatalogueTests
{
    private static readonly System.Type Vm = typeof(MainWindowViewModel);

    private static PropertyInfo? Prop(string name)
        => Vm.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

    [Fact]
    public void EveryCommandEntry_ResolvesToAnICommandProperty()
    {
        foreach (MenuActionCatalogue.Entry e in MenuActionCatalogue.AllEntries
                     .Where(e => e.EntryKind == MenuActionCatalogue.Kind.Command))
        {
            Assert.NotNull(e.CommandName);
            PropertyInfo? p = Prop(e.CommandName!);
            Assert.True(p is not null, $"{e.Id}: no property '{e.CommandName}' on MainWindowViewModel");
            Assert.True(typeof(ICommand).IsAssignableFrom(p!.PropertyType),
                $"{e.Id}: '{e.CommandName}' is not an ICommand");
        }
    }

    [Fact]
    public void EveryToggleEntry_ResolvesToABoolProperty()
    {
        foreach (MenuActionCatalogue.Entry e in MenuActionCatalogue.AllEntries
                     .Where(e => e.EntryKind == MenuActionCatalogue.Kind.Toggle))
        {
            Assert.NotNull(e.ToggleProperty);
            PropertyInfo? p = Prop(e.ToggleProperty!);
            Assert.True(p is not null, $"{e.Id}: no property '{e.ToggleProperty}' on MainWindowViewModel");
            Assert.Equal(typeof(bool), p!.PropertyType);
        }
    }

    [Fact]
    public void EveryGestureProperty_ResolvesToAStringProperty()
    {
        foreach (MenuActionCatalogue.Entry e in MenuActionCatalogue.AllEntries
                     .Where(e => e.GestureProperty is not null))
        {
            PropertyInfo? p = Prop(e.GestureProperty!);
            Assert.True(p is not null, $"{e.Id}: no gesture property '{e.GestureProperty}'");
            Assert.Equal(typeof(string), p!.PropertyType);
        }
    }

    [Fact]
    public void WorkshopTabAndCalculatorCommands_ExistAndAreCommands()
    {
        foreach (string name in new[] { "OpenWorkshopTabCommand", "OpenWorkshopCalculatorCommand" })
        {
            PropertyInfo? p = Prop(name);
            Assert.True(p is not null, $"missing '{name}' on MainWindowViewModel");
            Assert.True(typeof(ICommand).IsAssignableFrom(p!.PropertyType));
        }
    }

    // No auto-engine toggles in the pool — they live on the toolbar / Action menu,
    // not the right-click menu (user's rule). Sanity-check they're absent.
    [Fact]
    public void Catalogue_HasNoAutoEngineToggles()
    {
        Assert.Null(MenuActionCatalogue.Find("action.autocombat"));
        Assert.Null(MenuActionCatalogue.Find("action.sprint"));
        // But one-shot manual commands stay.
        Assert.NotNull(MenuActionCatalogue.Find("action.getall"));
    }

    [Fact]
    public void EveryDefaultEntry_ResolvesInTheCatalogue()
    {
        foreach (ContextMenuEntry entry in ContextMenuDefaults.Build()
                     .Where(x => x.Kind == ContextMenuEntryKind.Entry))
            Assert.True(MenuActionCatalogue.Find(entry.Id) is not null,
                $"default entry '{entry.Id}' isn't in the catalogue");
    }

    // Config round-trip: ApplyFrom → Snapshot preserves order, kind, id, and the
    // custom label; an empty DTO falls back to the built-in defaults.
    [Fact]
    public void Config_RoundTripsEntriesAndCustomLabels()
    {
        ContextMenuSettings dto = new()
        {
            Layout = new()
            {
                new ContextMenuEntry { Kind = ContextMenuEntryKind.Entry, Id = "view.party", Label = "My Party" },
                new ContextMenuEntry { Kind = ContextMenuEntryKind.Separator },
                new ContextMenuEntry { Kind = ContextMenuEntryKind.Entry, Id = "calc.hit" },
            },
        };
        ContextMenuConfig cfg = new();
        cfg.ApplyFrom(dto);
        ContextMenuSettings snap = cfg.Snapshot();

        Assert.Equal(3, snap.Layout!.Count);
        Assert.Equal("view.party", snap.Layout[0].Id);
        Assert.Equal("My Party", snap.Layout[0].Label);
        Assert.Equal(ContextMenuEntryKind.Separator, snap.Layout[1].Kind);
        Assert.Equal("calc.hit", snap.Layout[2].Id);
        Assert.Null(snap.Layout[2].Label);
    }

    // A user-defined folder with children deep-copies through the config (the
    // live layout must not alias the dto's child lists).
    [Fact]
    public void Config_RoundTripsFolderWithChildren()
    {
        ContextMenuSettings dto = new()
        {
            Layout = new()
            {
                new ContextMenuEntry
                {
                    Kind = ContextMenuEntryKind.Folder,
                    Label = "Tools",
                    Children = new()
                    {
                        new ContextMenuEntry { Kind = ContextMenuEntryKind.Entry, Id = "tools.bugreport" },
                        new ContextMenuEntry { Kind = ContextMenuEntryKind.Separator },
                        new ContextMenuEntry { Kind = ContextMenuEntryKind.Entry, Id = "tools.log", Label = "Log" },
                    },
                },
            },
        };
        ContextMenuConfig cfg = new();
        cfg.ApplyFrom(dto);
        ContextMenuSettings snap = cfg.Snapshot();

        ContextMenuEntry folder = Assert.Single(snap.Layout!);
        Assert.Equal(ContextMenuEntryKind.Folder, folder.Kind);
        Assert.Equal("Tools", folder.Label);
        Assert.NotNull(folder.Children);
        Assert.Equal(3, folder.Children!.Count);
        Assert.Equal("tools.bugreport", folder.Children[0].Id);
        Assert.Equal(ContextMenuEntryKind.Separator, folder.Children[1].Kind);
        Assert.Equal("Log", folder.Children[2].Label);
        Assert.NotSame(dto.Layout[0].Children, folder.Children);   // deep copy, not an alias
    }

    [Fact]
    public void Config_EmptyDto_FallsBackToDefaults()
    {
        ContextMenuConfig cfg = new();
        cfg.ApplyFrom(new ContextMenuSettings());
        Assert.Equal(ContextMenuDefaults.Build().Count, cfg.Layout.Count);
    }

    // The row VM stores a custom name only when it's non-blank; a blank name
    // round-trips to a null Label so the catalogue default shows.
    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Fight!", "Fight!")]
    public void Row_ToModel_NormalizesCustomLabel(string typed, string? expected)
    {
        MenuActionCatalogue.Entry def = MenuActionCatalogue.Find("view.party")!;
        var row = new ViewModels.Settings.ContextMenuRowViewModel(def) { CustomLabel = typed };
        Assert.Equal(expected, row.ToModel().Label);
        Assert.Equal("view.party", row.ToModel().Id);
    }
}
