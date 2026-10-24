using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MudPlay.Controls;
using Xunit;

namespace MudPlay.Tests;

// Two copies of a view built while not attached to a window, as each Settings window
// builds its sections for the search while the last window's copies are still
// uncollected (report paradigm-20261019-095332). Avalonia files their named radio
// groups in one app-wide registry; scoped, each copy keeps its own, and released,
// a closed window's copy leaves nothing there.
//
// A box and its model are joined as a two-way binding joins them on the client's UI
// thread, by hand (TwoWay). An Avalonia binding updated on any thread but the one
// that owns the UI dispatcher only posts the update there; in a test run nothing
// runs that dispatcher, and which thread owns it depends on which test reached it
// first, so a real binding here would deliver nothing in one run and everything in
// another. Each test checks that the first copy's box was checked, since unchecked,
// no box is unchecked across the copies and nothing here would be proven.
public sealed class RadioGroupScopeTests
{
    // The shape the Settings tabs bind a two-way choice to: one bool and its inverse,
    // as CombatSectionViewModel's stacked / each-monster pair.
    private sealed class Choice : INotifyPropertyChanged
    {
        private bool _each;
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Each
        {
            get => _each;
            set
            {
                if (_each == value) return;
                _each = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Each)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Stacked)));
            }
        }

        public bool Stacked
        {
            get => !Each;
            set => Each = !value;
        }
    }

    // A view as the XAML loader leaves it: named radios in place, scoped in the
    // constructor, then joined to its model.
    private static (StackPanel View, RadioButton Stacked, RadioButton Each) View(Choice model)
    {
        RadioButton stacked = new() { GroupName = "RoundTotalsMonsters" };
        RadioButton each = new() { GroupName = "RoundTotalsMonsters" };
        StackPanel view = new();
        view.Children.Add(stacked);
        view.Children.Add(each);
        RadioGroupScope.ToThisView(view);
        TwoWay(stacked, () => model.Stacked, v => model.Stacked = v, model);
        TwoWay(each, () => model.Each, v => model.Each = v, model);
        return (view, stacked, each);
    }

    // What `IsChecked="{Binding …, Mode=TwoWay}"` does on the UI thread: the model's
    // value now and on every change, and the box's every change written back.
    private static void TwoWay(RadioButton box, Func<bool> read, Action<bool> write, Choice model)
    {
        box.IsChecked = read();
        model.PropertyChanged += (_, _) => box.IsChecked = read();
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty && box.IsChecked is bool value) write(value);
        };
    }

    // Each test's controls are made on a thread of its own, so a view that spins is
    // timed out and fails just that test.
    private static void Run(Action action, [CallerMemberName] string test = "")
    {
        Exception? caught = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception ex) { caught = ex; }
        })
        {
            IsBackground = true,
            Name = test,
        };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), $"{test} never finished");
        Assert.Null(caught);
    }

    // Unscoped, the second copy's boxes uncheck the first copy's and write into its
    // model; on Avalonia 12.0.2 building the second copy also never returned. The
    // writes are counted, since the two flips the second copy makes can leave the
    // first copy's box where it started.
    [Fact]
    public void SecondCopy_BuildsAndLeavesTheFirstCopysChoiceAlone()
    {
        Choice first = new(), second = new();
        bool? firstShown = null, firstAfter = null;
        int firstWrites = 0;
        Run(() =>
        {
            var a = View(first);
            firstShown = a.Stacked.IsChecked;
            first.PropertyChanged += (_, _) => firstWrites++;
            var b = View(second);
            b.Each.IsChecked = true;
            firstAfter = a.Stacked.IsChecked;
        });

        Assert.True(firstShown);
        Assert.True(firstAfter);
        Assert.Equal(0, firstWrites);
        Assert.True(second.Each);
        Assert.True(first.Stacked);
    }

    // Within one copy the radios still exclude each other.
    [Fact]
    public void OneCopy_StillChoosesOne()
    {
        Choice model = new();
        bool? shown = null, stackedChecked = null;
        string? stackedGroup = null, eachGroup = null;
        Run(() =>
        {
            var view = View(model);
            shown = view.Stacked.IsChecked;
            view.Each.IsChecked = true;
            stackedChecked = view.Stacked.IsChecked;
            stackedGroup = view.Stacked.GroupName;
            eachGroup = view.Each.GroupName;
        });

        Assert.True(shown);
        Assert.False(stackedChecked);
        Assert.True(model.Each);
        Assert.NotEqual("RoundTotalsMonsters", eachGroup);
        Assert.Equal(stackedGroup, eachGroup);
    }

    // A released copy is out of the registry: a box checked under the name its group
    // had leaves the released copy's choice alone. Unreleased, that check unchecks
    // the released copy's box, as SecondCopy guards against.
    [Fact]
    public void ReleasedCopy_IsNoLongerInItsGroup()
    {
        Choice model = new();
        string? scopedName = null, releasedName = "unset";
        bool? shown = null, stillChecked = null;
        Run(() =>
        {
            var view = View(model);
            shown = view.Stacked.IsChecked;
            scopedName = view.Stacked.GroupName;
            RadioGroupScope.Release(view.View);
            releasedName = view.Stacked.GroupName;

            RadioButton stranger = new() { GroupName = scopedName };
            stranger.IsChecked = true;
            stillChecked = view.Stacked.IsChecked;
        });

        Assert.True(shown);
        Assert.NotNull(scopedName);
        Assert.Null(releasedName);
        Assert.True(stillChecked);
        Assert.True(model.Stacked);
    }

    // Radios a template makes are built only once their view is in a window, after
    // the constructor has run, so ToThisView can't rename them. These are reviewed:
    // neither is a pair bound to one bool and its inverse.
    private static readonly string[] TemplatedGroups =
    {
        "MonsterIntelWindow.axaml: MiRoundsAttack",
        "Settings/GeneralSectionView.axaml: TerminalColorScheme",
    };

    // Every view or window that names a radio group outside a template scopes it,
    // right after InitializeComponent, and a named group inside a template is one
    // of the reviewed ones.
    [Fact]
    public void EveryNamedRadioGroup_IsScopedOrAReviewedTemplate()
    {
        string views = Path.Combine(RepoRoot(), "Views");
        Regex scoped = new(@"InitializeComponent\(\);\s*(MudPlay\.)?Controls\.RadioGroupScope\.ToThisView\(this\);");
        List<string> scopedViews = new(), unscoped = new(), templated = new();
        foreach (string xaml in Directory.GetFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            string name = Path.GetRelativePath(views, xaml).Replace('\\', '/');
            bool needsScope = false;
            foreach (XElement radio in XDocument.Load(xaml).Descendants().Where(e => e.Name.LocalName == "RadioButton"))
            {
                if (radio.Attribute("GroupName")?.Value is not { Length: > 0 } group) continue;
                if (radio.Ancestors().Any(a => a.Name.LocalName.EndsWith("Template", StringComparison.Ordinal)))
                    templated.Add($"{name}: {group}");
                else
                    needsScope = true;
            }
            if (!needsScope) continue;
            scopedViews.Add(name);
            string codeBehind = xaml + ".cs";
            if (!File.Exists(codeBehind) || !scoped.IsMatch(File.ReadAllText(codeBehind)))
                unscoped.Add(name);
        }

        Assert.Empty(unscoped);
        Assert.Equal(TemplatedGroups, templated.Distinct().Order(StringComparer.Ordinal));
        Assert.Contains("Settings/CombatSectionView.axaml", scopedViews);
        Assert.Contains("Settings/EventEditDialog.axaml", scopedViews);
    }

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(here)!)!;
}
