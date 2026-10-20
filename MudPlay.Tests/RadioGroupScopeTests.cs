using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Data;
using MudPlay.Controls;
using Xunit;

namespace MudPlay.Tests;

// Two copies of a view built outside any window, as each Settings window builds its
// sections for the search while the last window's copies are still uncollected
// (report paradigm-20261019-095332). Avalonia files their named radio groups in one
// app-wide registry; scoped, each copy keeps its own.
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

    // Avalonia takes the first thread that makes one of its objects for its UI
    // thread, so the controls here are all made on one thread of their own, as the
    // client makes them on its UI thread. Apart from the test's thread, so a view
    // that spins fails its test instead of stalling the run.
    private static class UiThread
    {
        private static readonly BlockingCollection<Action> Work = new();

        static UiThread()
        {
            Thread thread = new(() => { foreach (Action work in Work.GetConsumingEnumerable()) work(); })
            {
                IsBackground = true,
                Name = "Avalonia UI (tests)",
            };
            thread.Start();
        }

        public static bool Run(Action action, out Exception? failed)
        {
            using ManualResetEventSlim done = new();
            Exception? caught = null;
            Work.Add(() =>
            {
                try { action(); }
                catch (Exception ex) { caught = ex; }
                finally { done.Set(); }
            });
            bool finished = done.Wait(TimeSpan.FromSeconds(10));
            failed = caught;
            return finished;
        }
    }

    // Unscoped, building the second copy never returns: the UI thread spins until
    // the client is killed.
    [Fact]
    public void SecondCopy_BuildsAndLeavesTheFirstCopysChoiceAlone()
    {
        Choice first = new(), second = new();
        bool finished = UiThread.Run(() =>
        {
            var a = View(first);
            var b = View(second);
            b.Each.IsChecked = true;
            GC.KeepAlive(a.View);
        }, out Exception? failed);

        Assert.True(finished, "building the second copy of the view never finished");
        Assert.Null(failed);
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
        bool finished = UiThread.Run(() =>
        {
            var view = View(model);
            view.Each.IsChecked = true;
            stackedChecked = view.Stacked.IsChecked;
            stackedGroup = view.Stacked.GroupName;
            eachGroup = view.Each.GroupName;
        }, out Exception? failed);

        Assert.True(finished, "building the view never finished");
        Assert.Null(failed);
        Assert.False(stackedChecked);
        Assert.True(model.Each);
        Assert.NotEqual("RoundTotalsMonsters", eachGroup);
        Assert.Equal(stackedGroup, eachGroup);
    }

    // Every view that names a radio group and can be built outside a window (any
    // view but a Window, whose own registry holds its buttons) scopes them.
    [Fact]
    public void EveryViewWithANamedRadioGroup_ScopesIt()
    {
        string views = Path.Combine(RepoRoot(), "Views");
        Regex named = new(@"<RadioButton\b[^>]*\bGroupName=""", RegexOptions.Singleline);
        List<string> checkedViews = new(), unscoped = new();
        foreach (string xaml in Directory.GetFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(xaml);
            if (!named.IsMatch(text) || Regex.IsMatch(text, @"^\s*<Window\b", RegexOptions.Multiline)) continue;
            string name = Path.GetRelativePath(views, xaml);
            checkedViews.Add(name);
            string codeBehind = xaml + ".cs";
            if (!File.Exists(codeBehind) || !File.ReadAllText(codeBehind).Contains("RadioGroupScope.ToThisView(this)"))
                unscoped.Add(name);
        }

        Assert.Empty(unscoped);
        Assert.Contains(Path.Combine("Settings", "CombatSectionView.axaml"), checkedViews);
    }

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(here)!)!;
}
