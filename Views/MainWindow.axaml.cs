using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Reactive;
using Avalonia.Threading;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Wires the terminal control's user-input event to the view-model and
// re-focuses the terminal whenever a connection is established (so the user
// can start typing right away).
public partial class MainWindow : Window
{
    private TextBlock? _combatTickLabel;
    // Set once the user (or programmatic shutdown) has confirmed exit, so the second Close call sails through.
    private bool _exitConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        AppServices.Current.WindowLayouts.AttachWindow(this, "main");
        // Wire the keybinds from the per-character KeybindingStore so
        // they track the user's overrides. Lazily resolves the VM on
        // AttachedToLogicalTree because DataContext is set externally
        // by App.OnFrameworkInitializationCompleted.
        GlobalHotkeys.AttachMain(this);

        // Forward keystrokes captured by the terminal control to whatever
        // view-model is currently set as DataContext.
        Terminal.UserInput += bytes =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.SendUserInput(bytes);
        };
        // Local-line-edit buffer — printable keystrokes accumulate
        // client-side and only flush to the wire on Enter. Engine
        // auto-sends (par poll, AutoParty invite, @health round-trip)
        // can fire freely without interleaving into half-typed input.
        Terminal.InputBuffer = AppServices.Current.InputBuffer;

        // Feed the terminal-host viewport size into the control so its
        // ScaleToFit math can grow the font to fill the window. The control is
        // measured with infinite available size inside the ScrollViewer, so it
        // can't read the window size itself — this is the channel. The observable
        // fires the current bounds on subscribe, so the initial size is seeded.
        TerminalScroll.GetObservable(Visual.BoundsProperty)
            .Subscribe(new AnonymousObserver<Rect>(b => Terminal.ViewportSize = b.Size));

        // Subscribe to VM PropertyChanged so we can react to IsConnected.
        // Hooking via DataContextChanged covers the case where the VM is
        // swapped at runtime — even though today it's set once in App.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is INotifyPropertyChanged pc)
                pc.PropertyChanged += OnVmPropertyChanged;
            if (DataContext is MainWindowViewModel vm)
            {
                vm.GameDataSets.CollectionChanged += OnGameDataSetsChanged;
                RebuildGameDataMenu(vm);
                vm.HelpLinks.CollectionChanged += OnHelpLinksChanged;
                RebuildHelpMenu(vm);
                vm.ContextMenu.Layout.CollectionChanged += OnContextMenuLayoutChanged;
                RebuildTerminalContextMenu(vm);
            }
        };

        Opened += (_, _) =>
        {
            _combatTickLabel = this.FindControl<TextBlock>("CombatTickLabel");
            AppServices.Current.Tick.CombatTickElapsed += OnCombatTickElapsed;
            // Put keyboard focus on the terminal from launch (not only on connect),
            // so the window's KeyBindings fire on the FIRST hotkey press. With focus
            // sitting on nothing (or on a toolbar button), the first press was being
            // spent taking focus and only the second registered. Deferred so it wins
            // over Avalonia's default initial focus assignment.
            Dispatcher.UIThread.Post(() => Terminal.Focus());
        };
        Closed += (_, _) =>
        {
            AppServices.Current.Tick.CombatTickElapsed -= OnCombatTickElapsed;
        };

        // Confirm-exit prompt + auto-save the loaded profile before exit.
        //
        // When the user has "Confirm exit" turned on in Settings → BBS,
        // intercept the first Closing fire, cancel it, run the modeless
        // confirm dialog async, then re-issue Close() if the user said
        // yes. The _exitConfirmed latch makes the second Close skip the
        // prompt so we don't loop. App-initiated shutdowns (none today)
        // would set _exitConfirmed=true before calling Close.
        //
        // ProfileService.Save no-ops on blank drafts (no name on disk to
        // write to) and when nothing is loaded, so the only path that
        // hits disk is the common case: a named profile is open. Saves
        // the current in-memory state so any per-session edits (BBS
        // pin, settings tab changes, etc.) survive a relaunch without
        // requiring the user to remember Ctrl+S.
        Closing += async (_, e) =>
        {
            if (!_exitConfirmed && AppServices.Current.Confirm.Settings.ConfirmExit)
            {
                e.Cancel = true;
                bool ok = await AppServices.Current.Confirm.ConfirmExitAsync();
                if (!ok) return;
                _exitConfirmed = true;
                Close();
                return;
            }

            // Clean shutdown forgets the party we were following: a deliberate
            // quit must NOT auto-rejoin on next launch (only a crash, which
            // never runs this handler, leaves the memory populated). Clearing
            // before Save persists the forget.
            if (AppServices.Current.Profile.Current is { } profile)
                profile.PendingReconnectLeader = null;

            try { AppServices.Current.Profile.Save(); }
            catch (Exception ex)
            {
                AppServices.Current.Log.Error("Profile",
                    $"Auto-save on exit failed: {ex.Message}");
            }
        };
    }

    // Pulse the Tick status-bar label amber for a brief beat each time
    // TickEngine fires. Class is added immediately, removed after a 200 ms
    // dispatcher delay so the user gets a visual heartbeat.
    private void OnCombatTickElapsed()
    {
        if (_combatTickLabel is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            _combatTickLabel.Classes.Add("Pulsing");
            DispatcherTimer.RunOnce(
                () => _combatTickLabel.Classes.Remove("Pulsing"),
                TimeSpan.FromMilliseconds(200));
        });
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // When we transition into "connected", move keyboard focus to the
        // terminal so typing goes to the BBS instead of the host textbox.
        if (e.PropertyName == nameof(MainWindowViewModel.IsConnected) &&
            DataContext is MainWindowViewModel vm && vm.IsConnected)
        {
            Terminal.Focus();
        }
    }

    private void OnGameDataSetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) RebuildGameDataMenu(vm);
    }

    private void OnHelpLinksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) RebuildHelpMenu(vm);
    }

    // Compose the Help menu: the "Help topics…" launcher, then one launch item
    // per user-editable website link (edited under Settings →
    // Toolbar + Shortcuts), then the active BBS's own site, then the static
    // Report / About actions. Same code-composition reason as the Game Data
    // menu — a MenuItem can't mix a bound dynamic list with inline static
    // children, so the whole list is assembled here on every HelpLinks change.
    private void RebuildHelpMenu(MainWindowViewModel vm)
    {
        HelpMenu.Items.Clear();

        HelpMenu.Items.Add(new MenuItem
        {
            Header  = "Help topics…",
            Command = vm.OpenHelpWindowCommand,
            [ToolTip.TipProperty] = "Searchable guide to features, how to use the client, and what each setting means.",
        });
        HelpMenu.Items.Add(new Separator());

        foreach (HelpWebsite link in vm.HelpLinks)
        {
            HelpMenu.Items.Add(new MenuItem
            {
                Header          = $"{link.Label} ↗",
                Command         = vm.OpenHelpLinkCommand,
                CommandParameter = link.Url,
            });
        }

        // BBS site — bound live so its visibility + enable state + tooltip track
        // the active BBS without a menu rebuild (ShowBbsWebsiteInHelp /
        // BbsWebsiteUrl / HasBbsWebsite re-raise on every BBS pin change). The
        // per-BBS show/hide toggle drives IsVisible; the URL presence drives
        // IsEnabled.
        MenuItem bbsSite = new()
        {
            Header  = "BBS site ↗",
            Command = vm.OpenBbsWebsiteCommand,
        };
        bbsSite.Bind(MenuItem.IsVisibleProperty, new Binding(nameof(vm.ShowBbsWebsiteInHelp)) { Source = vm });
        bbsSite.Bind(MenuItem.IsEnabledProperty, new Binding(nameof(vm.HasBbsWebsite)) { Source = vm });
        bbsSite.Bind(ToolTip.TipProperty, new Binding(nameof(vm.BbsWebsiteUrl))
        {
            Source          = vm,
            FallbackValue   = "Set a Website URL on the active BBS (Settings → Toolbar + Shortcuts) to enable.",
            TargetNullValue = "Set a Website URL on the active BBS (Settings → Toolbar + Shortcuts) to enable.",
        });
        HelpMenu.Items.Add(bbsSite);

        HelpMenu.Items.Add(new Separator());
        HelpMenu.Items.Add(new MenuItem
        {
            Header  = "Report an issue…",
            Command = vm.ReportIssueCommand,
        });
        HelpMenu.Items.Add(new Separator());
        HelpMenu.Items.Add(new MenuItem
        {
            Header  = "About MudPlay",
            Command = vm.OpenAboutCommand,
        });
    }

    // ----- Customizable terminal right-click menu -----------------------------
    // The ContextMenu's first three items (Favorites submenu, Recent submenu, and
    // their trailing separator) are fixed in XAML so their live bindings keep
    // working; everything after is rebuilt from AppServices.ContextMenu.Layout.
    // Each entry resolves through MenuActionCatalogue into a MenuItem — a command,
    // a toggle, a whole-menu submenu, a Workshop-tab link, or a calculator link —
    // reusing the same reflection bridge the toolbar/keybinds use for commands.
    private const int ContextMenuFixedLeadingItems = 3;
    private bool _ctxRebuildQueued;

    private void OnContextMenuLayoutChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // ApplyFrom clears + re-adds item-by-item, so coalesce the burst into a
        // single rebuild on the next dispatcher turn.
        if (_ctxRebuildQueued) return;
        _ctxRebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _ctxRebuildQueued = false;
            if (DataContext is MainWindowViewModel vm) RebuildTerminalContextMenu(vm);
        });
    }

    private void RebuildTerminalContextMenu(MainWindowViewModel vm)
    {
        if (TerminalContextMenu is null) return;
        ItemCollection items = TerminalContextMenu.Items;
        // Drop everything a previous build appended; keep the fixed leaders.
        while (items.Count > ContextMenuFixedLeadingItems)
            items.RemoveAt(items.Count - 1);

        foreach (ContextMenuEntry entry in vm.ContextMenu.Layout)
            if (BuildLayoutItem(entry, vm) is { } built) items.Add(built);
    }

    // One top-level layout entry → a menu control: a separator, a user-defined
    // folder (a named fly-out submenu of its Children), or a catalogue-backed
    // entry. Unknown ids and empty folders are dropped so nothing dead renders.
    private static Control? BuildLayoutItem(ContextMenuEntry entry, MainWindowViewModel vm)
    {
        switch (entry.Kind)
        {
            case ContextMenuEntryKind.Separator:
                return new Separator();
            case ContextMenuEntryKind.Folder:
            {
                MenuItem folder = new() { Header = string.IsNullOrWhiteSpace(entry.Label) ? "Folder" : entry.Label! };
                if (entry.Children is { } children)
                    foreach (ContextMenuEntry child in children)
                        if (BuildFolderChild(child, vm) is { } c) folder.Items.Add(c);
                return folder.Items.Count > 0 ? folder : null;   // hide an empty folder
            }
            default:   // Entry
                return MenuActionCatalogue.Find(entry.Id) is { } def
                    ? BuildContextMenuEntry(def, vm, entry.Label)
                    : null;
        }
    }

    // A folder's child — an Entry or Separator only (folders are one level deep).
    private static Control? BuildFolderChild(ContextMenuEntry child, MainWindowViewModel vm)
    {
        if (child.Kind == ContextMenuEntryKind.Separator) return new Separator();
        return MenuActionCatalogue.Find(child.Id) is { } def
            ? BuildContextMenuEntry(def, vm, child.Label)
            : null;
    }

    // Resolve one catalogue entry into a MenuItem, or null when it can't be built
    // (an unresolvable command). customLabel (the user's chosen name) overrides
    // the catalogue label when set.
    private static Control? BuildContextMenuEntry(MenuActionCatalogue.Entry def, MainWindowViewModel vm, string? customLabel = null)
    {
        string header = string.IsNullOrWhiteSpace(customLabel) ? def.Label : customLabel!;
        switch (def.EntryKind)
        {
            case MenuActionCatalogue.Kind.Toggle:
            {
                MenuItem item = new() { Header = header, ToggleType = MenuItemToggleType.CheckBox };
                if (def.Tooltip is not null) item[ToolTip.TipProperty] = def.Tooltip;
                item.Bind(MenuItem.IsCheckedProperty,
                    new Binding(def.ToggleProperty!) { Source = vm, Mode = BindingMode.TwoWay });
                return item;
            }
            case MenuActionCatalogue.Kind.WorkshopTab:
            {
                MenuItem item = new()
                {
                    Header = header,
                    Command = vm.OpenWorkshopTabCommand,
                    CommandParameter = def.Parameter,
                };
                if (def.Tooltip is not null) item[ToolTip.TipProperty] = def.Tooltip;
                return item;
            }
            case MenuActionCatalogue.Kind.Calculator:
            {
                MenuItem item = new()
                {
                    Header = header,
                    Command = vm.OpenWorkshopCalculatorCommand,
                    CommandParameter = def.Parameter,
                };
                if (def.Tooltip is not null) item[ToolTip.TipProperty] = def.Tooltip;
                return item;
            }
            default: // Command — reflection-resolve CommandName → ICommand, like the toolbar.
            {
                ICommand? cmd = def.CommandName is null
                    ? null
                    : vm.GetType().GetProperty(def.CommandName)?.GetValue(vm) as ICommand;
                if (cmd is null) return null;
                MenuItem item = new() { Header = header, Command = cmd };
                if (def.Tooltip is not null) item[ToolTip.TipProperty] = def.Tooltip;
                if (def.GestureProperty is not null)
                    item.Bind(MenuItem.InputGestureProperty, new Binding(def.GestureProperty) { Source = vm });
                return item;
            }
        }
    }

    // Compose the Game Data menu's items: every imported set on top
    // (each as a checkable MenuItem the user can click to activate),
    // a separator, then the static actions (Open Browser / Import .mdb
    // / Import loops). Avalonia's MenuItem can't mix ItemsSource-bound
    // dynamic children with inline static ones, so we assemble the
    // whole list in code on every change.
    private void RebuildGameDataMenu(MainWindowViewModel vm)
    {
        GameDataMenu.Items.Clear();

        foreach (GameDataSetMenuItem set in vm.GameDataSets)
        {
            GameDataMenu.Items.Add(new MenuItem
            {
                Header     = set.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked  = set.IsActive,
                Command    = set.SwitchCommand,
            });
        }

        if (vm.GameDataSets.Count > 0) GameDataMenu.Items.Add(new Separator());

        GameDataMenu.Items.Add(new MenuItem
        {
            Header       = "Open Browser…",
            InputGesture = new KeyGesture(Key.G, KeyModifiers.Control),
            Command      = vm.OpenGameDataBrowserCommand,
        });
        GameDataMenu.Items.Add(new Separator());
        GameDataMenu.Items.Add(new MenuItem
        {
            Header  = "Import .mdb…",
            Command = vm.ImportMdbCommand,
        });
        GameDataMenu.Items.Add(new MenuItem
        {
            Header  = "Import loops (MegaMUD .mp)…",
            Command = vm.ImportMegaMudLoopsCommand,
        });
        GameDataMenu.Items.Add(new MenuItem
        {
            Header  = "Manage Game Data…",
            Command = vm.OpenGameDataManagerCommand,
        });

        GameDataMenu.Items.Add(new Separator());
        GameDataMenu.Items.Add(new MenuItem
        {
            Header  = "Modify Blacklist…",
            Command = vm.OpenBlacklistEditorCommand,
        });
        GameDataMenu.Items.Add(new MenuItem
        {
            Header  = "Modify avoid/stash rooms…",
            Command = vm.OpenAvoidRoomsEditorCommand,
        });
    }
}
