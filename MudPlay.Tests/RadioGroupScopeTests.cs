using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Data;
using MudPlay.Controls;
using Xunit;

namespace MudPlay.Tests;

// Two copies of a view built while not attached to a window, as each Settings window
// builds its sections for the search while the last window's copies are still
// uncollected (report paradigm-20261019-095332). Avalonia files their named radio
// groups in one app-wide registry; scoped, each copy keeps its own, and released,
// a closed window's copy leaves nothing there.
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
    // constructor, then handed its model.
    private static (StackPanel View, RadioButton Stacked, RadioButton Each) View(Choice model)
    {
        RadioButton stacked = new() { GroupName = "RoundTotalsMonsters" };
        RadioButton each = new() { GroupName = "RoundTotalsMonsters" };
        stacked.Bind(RadioButton.IsCheckedProperty, new Binding(nameof(Choice.Stacked)) { Mode = BindingMode.TwoWay });
        each.Bind(RadioButton.IsCheckedProperty, new Binding(nameof(Choice.Each)) { Mode = BindingMode.TwoWay });
        StackPanel view = new();
        view.Children.Add(stacked);
        view.Children.Add(each);
        RadioGroupScope.ToThisView(view);
        view.DataContext = model;
        return (view, stacked, each);
    }

    // Avalonia binds its UI thread to the first thread that runs a binding, and every
    // later binding must run there, so all the controls here are made on one thread,
    // as the client makes them on its UI thread. It is not the test's own thread so
    // that a view that spins can be timed out: its test fails, and the tests after it
    // fail at once, naming it, instead of each waiting on the stuck thread.
    private static class UiThread
    {
        private static readonly BlockingCollection<Action> Work = new();
        private static string? _stuckIn;

        static UiThread()
        {
            Thread thread = new(() => { foreach (Action work in Work.GetConsumingEnumerable()) work(); })
            {
                IsBackground = true,
                Name = "Avalonia UI (tests)",
            };
            thread.Start();
        }

        public static void Run(Action action, [CallerMemberName] string test = "")
        {
            Assert.True(_stuckIn is null, $"the UI thread is still stuck in {_stuckIn}");
            using ManualResetEventSlim done = new();
            Exception? caught = null;
            Work.Add(() =>
            {
                try { action(); }
                catch (Exception ex) { caught = ex; }
                finally { done.Set(); }
            });
            if (!done.Wait(TimeSpan.FromSeconds(10)))
            {
                _stuckIn = test;
                Assert.Fail($"{test} never finished on the UI thread");
            }
            Assert.Null(caught);
        }
    }

    // Unscoped, building the second copy never returns: the UI thread spins until
    // the client is killed.
    [Fact]
    public void SecondCopy_BuildsAndLeavesTheFirstCopysChoiceAlone()
    {
        Choice first = new(), second = new();
        UiThread.Run(() =>
        {
            var a = View(first);
            var b = View(second);
            b.Each.IsChecked = true;
            GC.KeepAlive(a.View);
        });

        Assert.True(second.Each);
        Assert.True(first.Stacked);
    }

    // Within one copy the radios still exclude each other.
    [Fact]
    public void OneCopy_StillChoosesOne()
    {
        Choice model = new();
        bool? stackedChecked = null;
        string? stackedGroup = null, eachGroup = null;
        UiThread.Run(() =>
        {
            var view = View(model);
            view.Each.IsChecked = true;
            stackedChecked = view.Stacked.IsChecked;
            stackedGroup = view.Stacked.GroupName;
            eachGroup = view.Each.GroupName;
        });

        Assert.False(stackedChecked);
        Assert.True(model.Each);
        Assert.NotEqual("RoundTotalsMonsters", eachGroup);
        Assert.Equal(stackedGroup, eachGroup);
    }

    // A released copy is out of the registry: a box checked under the name its group
    // had no longer unchecks the released copy's choice.
    [Fact]
    public void ReleasedCopy_IsNoLongerInItsGroup()
    {
        Choice model = new();
        string? scopedName = null, releasedName = "unset";
        bool? stillChecked = null;
        UiThread.Run(() =>
        {
            var view = View(model);
            scopedName = view.Stacked.GroupName;
            RadioGroupScope.Release(view.View);
            releasedName = view.Stacked.GroupName;

            RadioButton stranger = new() { GroupName = scopedName };
            stranger.IsChecked = true;
            stillChecked = view.Stacked.IsChecked;
        });

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
