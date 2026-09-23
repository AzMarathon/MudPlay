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

    // Latched by the app-initiated exits (File → Quit, the self-updater's restart)
    // before they call Shutdown. Those paths ask for confirmation themselves, and a
    // forced Shutdown discards this handler's e.Cancel anyway — so without the latch
    // the prompt would appear over a dying app AND swallow the save below it.
    public void MarkExitConfirmed() => _exitConfirmed = true;

    // The free-floating first-run setup card (shown left of the main window).
    private FirstRunTutorialWindow? _tutorialWindow;

    // Show / hide the floating tour card as the tour activates / deactivates.
    private void UpdateTutorialWindow(MainWindowViewModel mvm)
    {
        if (mvm.Tutorial.IsActive)
        {
            if (_tutorialWindow is null)
            {
                _tutorialWindow = new FirstRunTutorialWindow { DataContext = mvm.Tutorial };
                _tutorialWindow.Closed += (_, _) => _tutorialWindow = null;
                _tutorialWindow.Show(this);   // owned by main → closes with it
                Activate();                   // keep keyboard focus on the terminal
                Dispatcher.UIThread.Post(PositionTutorialWindow, DispatcherPriority.Background);
            }
            else
            {
                PositionTutorialWindow();
            }
        }
        else
        {
            _tutorialWindow?.Close();
            _tutorialWindow = null;
        }
    }

    // Park the card just off the main window's left edge, top-aligned. Physical
    // pixels (Position is screen space), so the fixed 300-DIP width is scaled.
    private void PositionTutorialWindow()
    {
        if (_tutorialWindow is null) return;
        double scaling = _tutorialWindow.DesktopScaling;
        int w = (int)System.Math.Round(300 * scaling);
        int gap = (int)System.Math.Round(8 * scaling);
        _tutorialWindow.Position = new PixelPoint(Position.X - w - gap, Position.Y);
    }

    // The dynamic Game Data → Import .mdb item, tracked so the tour can glow it
    // (its action line ticks from the ImportMdb command; the static Profile
    // Management item is highlighted via a XAML class binding).
    private MenuItem? _importMdbMenuItem;

    private void ApplyImportMdbHighlight(MainWindowViewModel mvm)
    {
        if (_importMdbMenuItem is null) return;
        bool on = mvm.Tutorial.HighlightImportMdb;
        if (on && !_importMdbMenuItem.Classes.Contains("tutTarget"))
            _importMdbMenuItem.Classes.Add("tutTarget");
        else if (!on)
            _importMdbMenuItem.Classes.Remove("tutTarget");
    }

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
                // The Sys Goto flyout is capability-gated at build time, so rebuild the
                // whole menu when the power / table could have changed (profile load,
                // or a credentials save which fires ProfileMutated) — otherwise ticking
                // the Sysop-goto checkbox wouldn't surface the flyout until the layout
                // was next dirtied.
                MudPlay.Services.AppServices.Current.Profile.ProfileLoaded += _ => RebuildTerminalContextMenu(vm);
                MudPlay.Services.AppServices.Current.Profile.ProfileMutated += _ => RebuildTerminalContextMenu(vm);
                vm.CombatProfileItems.CollectionChanged += OnCombatProfileItemsChanged;
                RebuildProfilesMenu(vm);
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

            // First-run setup tour. The step card is a free-floating window shown
            // to the LEFT of the main window (never over the terminal); show/hide
            // it as the tour activates, and keep it glued to our left edge as we
            // move. Register the force-show hook (Help = real outstanding steps,
            // Program Log test button = demo), then auto-show when a brand-new
            // install is still missing a prerequisite and it hasn't been dismissed.
            if (DataContext is MainWindowViewModel mvm)
            {
                mvm.Tutorial.PropertyChanged += (_, ev) =>
                {
                    if (ev.PropertyName == nameof(FirstRunTutorialViewModel.IsActive))
                        UpdateTutorialWindow(mvm);
                    if (ev.PropertyName is nameof(FirstRunTutorialViewModel.IsActive)
                        or nameof(FirstRunTutorialViewModel.HighlightImportMdb))
                        ApplyImportMdbHighlight(mvm);
                    // Publish the action the tour wants next so the Profile
                    // Management / Settings windows can glow the right control.
                    if (ev.PropertyName is nameof(FirstRunTutorialViewModel.IsActive)
                        or nameof(FirstRunTutorialViewModel.CurrentActionKey))
                        AppServices.Current.SetCurrentTourAction(
                            mvm.Tutorial.IsActive ? mvm.Tutorial.CurrentActionKey : null);
                };
                PositionChanged += (_, _) => PositionTutorialWindow();

                // Let other windows report deep tour steps back to the tour VM.
                AppServices.Current.NotifyTourAction = key =>
                    Dispatcher.UIThread.Post(() => mvm.Tutorial.NotifyActionDone(key));

                AppServices.Current.StartFirstRunTutorial = demo =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        Activate();
                        if (demo) mvm.Tutorial.StartDemo();
                        else mvm.Tutorial.Start();
                    });

                bool dismissed = AppServices.Current.Settings.Current.FirstRunTutorialDismissed;
                bool missing = mvm.Tutorial.AnyPrerequisiteMissing;
                if (!dismissed && missing)
                {
                    AppServices.Current.Log.Info("Tutorial", "first-run setup tour: showing (a prerequisite is missing)");
                    Dispatcher.UIThread.Post(mvm.Tutorial.Start);
                }
                else
                {
                    AppServices.Current.Log.Info("Tutorial",
                        $"first-run setup tour: not shown ({(dismissed ? "dismissed" : "all prerequisites present")})");
                }
            }
        };
        // Returning to the main window (e.g. after adding a BBS in Profile
        // Management) re-checks the tour's steps so a finished one shows its check
        // and the tour advances on its own.
        Activated += (_, _) =>
        {
            if (DataContext is MainWindowViewModel mvm) mvm.Tutorial.Refresh();
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
        // prompt so we don't loop. App-initiated shutdowns (File → Quit,
        // the self-updater) call MarkExitConfirmed first — they prompt on
        // their own, before the shutdown that would ignore a cancel here.
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

    private void OnCombatProfileItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) RebuildProfilesMenu(vm);
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
        MenuItem firstRunSetup = new()
        {
            Header = "First-time setup…",
            [ToolTip.TipProperty] = "Replay the guided setup tour: import game data, add a BBS + character, connect.",
        };
        firstRunSetup.Click += (_, _) => vm.Tutorial.Start();
        HelpMenu.Items.Add(firstRunSetup);
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
            Header  = "Update the Client",
            Command = vm.OpenUpdateCommand,
            [ToolTip.TipProperty] = "See whether a newer build is available and install it.",
        });
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
    private const int ContextMenuFixedLeadingItems = 0;
    private bool _ctxRebuildQueued;
    // Walk-flyout (Favorites / Recent) collection subscriptions from the current
    // build, dropped and re-made on each rebuild so they don't accumulate.
    private readonly List<(INotifyCollectionChanged Src, NotifyCollectionChangedEventHandler Handler)> _walkFlyoutSubs = new();

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
        // Detach the previous build's walk-flyout subscriptions before rebuilding.
        foreach ((INotifyCollectionChanged src, NotifyCollectionChangedEventHandler handler) in _walkFlyoutSubs)
            src.CollectionChanged -= handler;
        _walkFlyoutSubs.Clear();
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
    private Control? BuildLayoutItem(ContextMenuEntry entry, MainWindowViewModel vm)
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
                // Show even an empty folder (as a submenu with a disabled hint) so a
                // just-created folder doesn't silently vanish from the menu.
                if (folder.Items.Count == 0)
                    folder.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
                return folder;
            }
            default:   // Entry
                return MenuActionCatalogue.Find(entry.Id) is { } def
                    ? BuildContextMenuEntry(def, vm, entry.Label)
                    : null;
        }
    }

    // A folder's child — an Entry or Separator only (folders are one level deep).
    private Control? BuildFolderChild(ContextMenuEntry child, MainWindowViewModel vm)
    {
        if (child.Kind == ContextMenuEntryKind.Separator) return new Separator();
        return MenuActionCatalogue.Find(child.Id) is { } def
            ? BuildContextMenuEntry(def, vm, child.Label)
            : null;
    }

    // Whether a catalogue entry's capability id is currently granted, so the menu
    // renders it. Only the sysop-goto power gates a menu entry today.
    private static bool IsMenuCapabilityOn(string capability) => capability switch
    {
        "sysop.goto" => MudPlay.Services.AppServices.Current.SysopGoto.Enabled,
        _ => true,
    };

    // Resolve one catalogue entry into a MenuItem, or null when it can't be built
    // (an unresolvable command, or a capability that's off). customLabel (the user's
    // chosen name) overrides the catalogue label when set.
    private Control? BuildContextMenuEntry(MenuActionCatalogue.Entry def, MainWindowViewModel vm, string? customLabel = null)
    {
        string header = string.IsNullOrWhiteSpace(customLabel) ? def.Label : customLabel!;
        // Capability-gated entries (e.g. the Sys Goto flyout) are omitted from the
        // live menu when their power is off — the entry still lives in the editor pool,
        // so the user can place it before enabling the power. Same "return null →
        // silently omitted" contract the unresolvable-Command path below already uses.
        if (def.Capability is { } cap && !IsMenuCapabilityOn(cap))
            return null;
        switch (def.EntryKind)
        {
            case MenuActionCatalogue.Kind.WalkFlyout:
            {
                // The GOTO Favorites / Recent-destinations fly-out: a submenu of a
                // live ObservableCollection, rendered by the WalkFlyoutItemTheme the
                // menu declares. Since the user deliberately placed it, it ALWAYS
                // shows — a new/default profile with an empty list surfaces a
                // disabled "(none yet)" slot instead of vanishing. Items rebuild on
                // every collection change (subscriptions are dropped on the next
                // menu rebuild — see RebuildTerminalContextMenu).
                System.Collections.ObjectModel.ObservableCollection<FavoriteMenuItem> source = def.Parameter switch
                {
                    "recent" => vm.RecentDestinations,
                    "sysgotos" => vm.SysopGotoItems,
                    _ => vm.Favorites,
                };
                MenuItem item = new() { Header = header };
                if (def.Tooltip is not null) item[ToolTip.TipProperty] = def.Tooltip;
                if (TerminalContextMenu?.TryFindResource("WalkFlyoutItemTheme", out object? themeObj) == true
                    && themeObj is Avalonia.Styling.ControlTheme theme)
                    item.ItemContainerTheme = theme;

                void Populate()
                {
                    item.Items.Clear();
                    if (source.Count == 0)
                        item.Items.Add(new MenuItem { Header = "(none yet)", IsEnabled = false });
                    else
                        foreach (FavoriteMenuItem f in source) item.Items.Add(f);
                }
                Populate();
                NotifyCollectionChangedEventHandler handler = (_, _) => Populate();
                source.CollectionChanged += handler;
                _walkFlyoutSubs.Add((source, handler));
                return item;
            }
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
            case MenuActionCatalogue.Kind.SettingsTab:
            {
                MenuItem item = new()
                {
                    Header = header,
                    Command = vm.OpenSettingsTabCommand,
                    CommandParameter = def.Parameter,
                };
                if (def.Tooltip is not null) item[ToolTip.TipProperty] = def.Tooltip;
                return item;
            }
            case MenuActionCatalogue.Kind.GameDataSection:
            {
                MenuItem item = new()
                {
                    Header = header,
                    Command = vm.OpenGameDataSectionCommand,
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
        MenuItem importMdb = new()
        {
            Header  = "Import .mdb…",
            Command = vm.ImportMdbCommand,
        };
        _importMdbMenuItem = importMdb;
        ApplyImportMdbHighlight(vm);   // re-assert the glow after a menu rebuild
        GameDataMenu.Items.Add(importMdb);
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

    // Rebuild the Action → Profiles fly-out from the shared CombatProfileItems —
    // one checkable "N) name" row per profile, the active one checked. Hidden until
    // a profile exists.
    private void RebuildProfilesMenu(MainWindowViewModel vm)
    {
        ProfilesMenu.Items.Clear();
        foreach (CombatProfileMenuItem p in vm.CombatProfileItems)
        {
            ProfilesMenu.Items.Add(new MenuItem
            {
                Header     = p.Display,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked  = p.IsActive,
                Command    = p.SwitchCommand,
            });
        }
        ProfilesMenu.IsVisible = vm.HasCombatProfiles;
    }

    // Toolbar left-click: the Combat-Profile MENU button opens a fly-out of the
    // profiles ("N) name", active checked). The CYCLE button's left-click runs its
    // Command (next profile); every other button is unaffected.
    private void OnToolbarButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not Services.ToolbarButtonItem item
            || DataContext is not MainWindowViewModel vm
            || item.ActionId != "CombatProfileMenu") return;

        MenuFlyout flyout = new();
        foreach (CombatProfileMenuItem p in vm.CombatProfileItems)
        {
            flyout.Items.Add(new MenuItem
            {
                Header     = p.Display,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked  = p.IsActive,
                Command    = p.SwitchCommand,
            });
        }
        flyout.ShowAt(button);
    }

    // Toolbar right-click: the Combat-Profile CYCLE button steps to the PREVIOUS
    // profile (its left-click / Command steps to the next).
    private void OnToolbarButtonPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Right) return;
        if (sender is Button button
            && button.DataContext is Services.ToolbarButtonItem { ActionId: "CycleCombatProfile" }
            && DataContext is MainWindowViewModel vm)
            vm.CycleCombatProfileBack();
    }
}
