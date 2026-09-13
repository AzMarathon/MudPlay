using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// "BBS" tab. Owns the list of saved BBS records (globally shared across every
// character) and the field-editor for whichever one is selected. Per-character
// credentials (username, password, menu-nav sequence) live on the character
// profile.
//
// Apply walks the cached in-memory BBS profiles and persists every dirty one,
// then commits the credential edits staged for every board the user touched —
// both halves of the tab honour edits made across several boards in one visit,
// not just the one selected when OK was pressed. Discard drops both caches and
// reloads the currently-selected BBS from disk. Adding / deleting a BBS commits
// immediately (those are structural, not field-level edits — the OK / Cancel
// commit only covers field tweaks).
public sealed partial class BbsSectionViewModel : SettingsSectionViewModel
{
    private readonly BbsProfileStore _bbsStore;
    private readonly ProfileService _profile;
    private readonly PasswordProtector _passwords;
    private readonly DisplayConfig _display;
    private readonly SettingsService _globalSettings;
    private readonly Dictionary<string, BbsProfile> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingPassword;          // null = unchanged; "" = clear; else write
    private bool _suppressDirty = true;
    private bool _dirty;
    private Control? _view;

    // Per-BBS staging for the credential block, the counterpart to _loaded. The
    // BBS-profile fields survive a click to another board because PushToCache
    // writes them into _loaded on every keystroke; credentials are read and
    // written as one bundle, so instead of pushing per-field they're captured
    // here the moment the selection leaves a board. Without this, editing a login
    // and then clicking a different BBS before OK dropped the edit silently —
    // Apply only ever committed whichever board happened to be selected.
    private readonly Dictionary<string, StagedCredentials> _stagedCredentials =
        new(StringComparer.OrdinalIgnoreCase);

    // Whether the credential block has been edited since the current board was
    // loaded. Gates staging so merely clicking through boards doesn't author a
    // credential entry on every one of them.
    private bool _credentialsTouched;

    // One board's pending credential edits. The password is held encrypted — the
    // plaintext is deliberately short-lived (see LoadCredentialsFor), and staging
    // it across several boards would otherwise keep every typed password in
    // memory for the life of the window. PasswordTouched separates "leave the
    // stored password alone" from "clear it".
    private sealed class StagedCredentials
    {
        public string Username = string.Empty;
        public bool PasswordTouched;
        public string? EncryptedPassword;
        public bool SysopStatus;
        public bool SysopGodLives;
        public bool SysopGoto;
        public List<MenuStep> MenuNavSteps = new();
        public List<SysopGotoLocation> SysopGotos = new();
    }

    public override string Id => "bbs";
    public override string Title => "BBS + Display";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "BBS", "Host", "Port", "Telnet", "Redial", "Cleanup", "Reconnect",
        "Sysop", "Sys Goto", "Terminal", "Cols", "Rows", "NAWS", "Connection",
        "Game entry command", "Game exit command", "Enter realm", "Logoff",
        "Player dies at", "Death floor", "Bleeding out", "Dropped", "Hangup HP",
        "Auto-refine death floor", "Trace death floor", "Slow death", "Learn floor",
        "Disconnect pattern", "Party disconnect", "Logoff pattern", "Logs off",
        "Player disconnect line",
        "Display", "Scrollback", "Backscroll", "Buffer",
        "Confirm", "Confirm exit", "Confirm hangup", "Confirm save", "Confirm delete",
    };

    public override Control View => _view ??= new BbsSectionView { DataContext = this };

    // Names of every saved BBS profile (left rail of the tab).
    public ObservableCollection<string> AvailableBbsNames { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private string? _selectedBbsName;

    public bool HasSelection => SelectedBbsName is not null;

    // Max-redials / redial-pause tickers grey out while InfiniteRetries forces
    // its own count + pause; the XAML binds their IsEnabled here.
    public bool RedialTickersEnabled => !InfiniteRetries;

    // ----- Editable fields, populated from the selected BbsProfile -----
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = 23;
    [ObservableProperty] private int _maxRedials = 3;
    [ObservableProperty] private int _redialPauseSeconds = 5;
    [ObservableProperty] private bool _infiniteRetries;
    [ObservableProperty] private int _cleanupPeriodMinutes;
    [ObservableProperty] private int _noResponseTimeoutSeconds;
    [ObservableProperty] private bool _reconnectOnFailedConnect;
    [ObservableProperty] private bool _reconnectOnCarrierLost;
    [ObservableProperty] private bool _reconnectOnNoResponse;
    [ObservableProperty] private bool _reconnectAfterCleanup;
    [ObservableProperty] private bool _sysopStatus;
    [ObservableProperty] private bool _sysopGodLives;
    [ObservableProperty] private bool _sysopGoto;
    [ObservableProperty] private int _terminalCols = 80;
    [ObservableProperty] private int _terminalRows = 25;
    [ObservableProperty] private int _scrollbackLines = 4_000;
    [ObservableProperty] private int _backscrollWheelLines = 5;

    // ----- Game-menu commands (per-BBS) -----
    // The two main-menu picks for entering / leaving the realm. Stored
    // per-BBS because the menu key bindings are a property of the realm /
    // front-end, not the character.
    [ObservableProperty] private string _gameEntryCommand = "E";
    [ObservableProperty] private string _gameExitCommand = "=x";

    // ----- Realm mechanics (per-BBS) -----
    // The negative-HP floor at which a character actually dies (0 HP only drops
    // you into a revivable bleed-out). The emergency auto-hangup reads it to
    // keep firing through the whole bleeding-out window. Seeded at the standard
    // -25.
    [ObservableProperty] private int _playerDiesAtHp = -25;

    // When on, the death-floor tracer refines PlayerDiesAtHp from observed slow
    // deaths (a bleed-out lands right at the true floor). Off pins the manual
    // value. Default on.
    [ObservableProperty] private bool _autoRefineDeathFloor = true;

    // Nightly boss-cleanup wall-clock time ("HH:mm" in CleanupTimeZone) + its zone.
    // Drives the DEAD/ALIVE state of "Respawns @ Cleanup" bosses on the Bosses tab.
    // Default 21:00 in the computer's own zone (auto-detected); the zone is a
    // dropdown the user can override.
    [ObservableProperty] private string _cleanupTimeOfDay = "21:00";
    [ObservableProperty] private string _cleanupTimeZoneId = TimeZoneInfo.Local.Id;

    // Every system time-zone id (auto-detected local zone included), sorted — the
    // options for the cleanup-zone dropdown.
    public IReadOnlyList<string> TimeZoneIds { get; } = BuildTimeZoneIds();

    private static IReadOnlyList<string> BuildTimeZoneIds()
    {
        var ids = TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).ToList();
        if (!ids.Contains(TimeZoneInfo.Local.Id, StringComparer.OrdinalIgnoreCase))
            ids.Add(TimeZoneInfo.Local.Id);
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    // Board-specific player-disconnect line (see BbsProfile.DisconnectPattern).
    // Optional literal pattern — {name} captures the disconnecting player, *
    // swallows a varying run. Empty = only the built-in "just disconnected" /
    // "just hung up" forms are watched.
    [ObservableProperty] private string? _disconnectPattern;

    // Per-BBS label for the top (runic) denomination — some realms rename it,
    // which changes both the coin wording the server sends and the keyword the
    // client keys currency commands on. Blank falls back to "runic" on save.
    [ObservableProperty] private string _runicCurrencyName = "runic";

    // ----- Per-character credentials -----
    // True when any character profile is loaded — including unsaved drafts.
    // Credentials, sysop flag, and menu nav all bind against the in-memory
    // CharacterProfile; the password is encrypted with the per-user .credkey (not
    // anything keyed on the profile name) so an unsaved draft can carry them
    // forward into its first Save just fine. Only used now to dim the credentials
    // block when literally no profile object exists (a state we never actually
    // reach at runtime, but the guard keeps designer-time previews honest).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsHint))]
    [NotifyPropertyChangedFor(nameof(IsCredentialsHintWarning))]
    private bool _hasProfile;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _showPassword;

    // Suicide-password display only — captured passively by
    // SuicidePasswordTracker when the user runs `set suicide` in-game.
    // No editor; the BBS-tab field is read-only and hidden when nothing
    // is stored. ShowSuicidePassword toggles the obfuscation char.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuicidePassword))]
    private string _suicidePassword = string.Empty;

    [ObservableProperty] private bool _showSuicidePassword;

    // True when the loaded profile carries a stored suicide password.
    public bool HasSuicidePassword => !string.IsNullOrEmpty(SuicidePassword);

    // ----- Confirm prompts (Global tier — install-wide UX preferences) -----
    // Persisted in GlobalSettings.Settings["Confirm"] and mirrored live
    // onto AppServices.Current.Confirm by ApplyConfirmFromGlobalSettings.
    // Explicit `= false` defaults — fresh installs / first-open of this
    // tab render every checkbox unchecked so no nagging dialogs land on
    // a user who hasn't asked for them.
    [ObservableProperty] private bool _confirmExit = false;
    [ObservableProperty] private bool _confirmHangup = false;
    [ObservableProperty] private bool _confirmSaveSettings = false;
    [ObservableProperty] private bool _confirmDeletes = false;

    // Editable rows for the per-character menu-nav sequence.
    public ObservableCollection<MenuStepEditorViewModel> MenuNavSteps { get; } = new();

    // Editable rows for the per-character `sys goto` location table (shown/enabled
    // only when SysopGoto is on).
    public ObservableCollection<SysopGotoRowViewModel> SysopGotos { get; } = new();

    // Logon sequences from other saved characters, offered as import sources so a
    // new (or additional) character doesn't have to retype a flow another
    // character already worked out. Every character is listed, not just ones on
    // this BBS — some BBSes share a front-end, so a cross-BBS flow is often a
    // mostly-right starting point. Rebuilt whenever the selected BBS or loaded
    // profile changes.
    public ObservableCollection<MenuNavImportOption> ImportSourceOptions { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportMenuNavCommand))]
    private MenuNavImportOption? _selectedImportSource;

    // Drives the picker's enabled state — false hides / greys the import row when
    // no other character has any logon steps to borrow.
    public bool HasImportSources => ImportSourceOptions.Count > 0;

    // Helper text under the credentials section.
    public string CredentialsHint
    {
        get
        {
            if (!HasProfile)
                return "Load or create a profile to edit credentials.";
            return _profile.CurrentProfileName is { } name
                ? $"For character: {name}"
                : "(default profile - You haven't saved this profile)";
        }
    }

    // True when the credentials hint should be drawn in a warning color (e.g.,
    // red) — currently only for the unsaved-draft case, so the user can see at a
    // glance that their edits won't persist until they Save / Save As.
    public bool IsCredentialsHintWarning =>
        HasProfile && _profile.CurrentProfileName is null;

    public BbsSectionViewModel(
        BbsProfileStore bbsStore,
        ProfileService profile,
        PasswordProtector passwords,
        DisplayConfig display,
        SettingsService globalSettings)
    {
        ArgumentNullException.ThrowIfNull(bbsStore);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(passwords);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(globalSettings);
        _bbsStore = bbsStore;
        _profile = profile;
        _passwords = passwords;
        _display = display;
        _globalSettings = globalSettings;

        Action<CharacterProfile> onProfileLoaded = _ => RefreshProfileState();
        // SuicidePasswordTracker writes a new encrypted blob and
        // calls NotifyMutated on commit; pick that up so the
        // Settings → BBS field reflects the freshly-captured value
        // without requiring the user to reload the section.
        Action<CharacterProfile> onProfileMutated = _ => RefreshSuicidePassword();
        _profile.ProfileLoaded += onProfileLoaded;
        _profile.ProfileClosed += RefreshProfileState;
        _profile.ProfileMutated += onProfileMutated;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= onProfileLoaded;
            _profile.ProfileClosed -= RefreshProfileState;
            _profile.ProfileMutated -= onProfileMutated;
        });
        RefreshProfileState();
        LoadConfirmFromGlobalSettings();

        ReloadBbsList();
        // Default selection to the loaded character's active BBS when it's
        // in the list — re-entering settings should land on the BBS the
        // user is currently dialed at.
        string? preferred = _profile.CurrentBbsName;
        SelectedBbsName = preferred is not null && AvailableBbsNames.Contains(preferred)
            ? preferred
            : AvailableBbsNames.FirstOrDefault();
        // OnSelectedBbsNameChanged short-circuits while _suppressDirty is
        // true (so the initial property assignment doesn't mark dirty), so
        // we have to call ReloadSelected ourselves here. Without this, the
        // editor stays blank until the user clicks a different BBS — even
        // when there's only one in the list and it's already selected.
        if (SelectedBbsName is not null) ReloadSelected();
        _suppressDirty = false;

        // If the auto-picked selection doesn't match the profile's current
        // pin — common case: blank draft (BbsName null) on first open with
        // one BBS in the list — mark dirty so OK stamps the pin even when
        // the user doesn't touch any field.
        if (!string.Equals(SelectedBbsName, _profile.CurrentBbsName, StringComparison.OrdinalIgnoreCase))
        {
            Dirty();
        }
    }

    public override void Apply()
    {
        // Rename pass: if the Name field differs from the selected key, the
        // user retitled this BBS. Move the on-disk file + cache entry and
        // refresh the selection so the list shows the new name.
        if (SelectedBbsName is { } oldName
            && !string.IsNullOrWhiteSpace(Name)
            && !string.Equals(oldName, Name, StringComparison.OrdinalIgnoreCase))
        {
            RenameSelected(oldName, Name);
        }

        foreach (BbsProfile profile in _loaded.Values)
        {
            // The website is now edited under Settings → Toolbar + Shortcuts and
            // may have just been written there in the same OK. Re-read the
            // on-disk value into this cached copy so our save folds it in rather
            // than clobbering it with the WebsiteUrl loaded at selection time.
            if (_bbsStore.Get(profile.Name) is { WebsiteUrl: var url })
                profile.WebsiteUrl = url;
            _bbsStore.Save(profile);
        }

        ApplyToCurrentProfile();
        SaveConfirmToGlobalSettings();

        ResetCredentialStaging();
        ClearDirty();
    }

    // Drop every staged credential edit and the in-flight plaintext password.
    // Called once the edits have been committed (Apply) or abandoned (Discard,
    // character swap) — holding them past that point would re-commit stale
    // values onto whatever profile is loaded next.
    private void ResetCredentialStaging()
    {
        _stagedCredentials.Clear();
        _credentialsTouched = false;
        _pendingPassword = null;
    }

    // Hydrate the four Confirm* observables from the Global-tier settings file.
    // Runs once at ctor time; Discard re-runs it to roll back unsaved edits.
    private void LoadConfirmFromGlobalSettings()
    {
        ConfirmSettings dto = new();
        Dictionary<string, System.Text.Json.JsonElement>? bucket =
            _globalSettings.Current.Settings;
        if (bucket is not null
            && bucket.TryGetValue("Confirm", out System.Text.Json.JsonElement json))
        {
            try
            {
                dto = System.Text.Json.JsonSerializer.Deserialize<ConfirmSettings>(json) ?? new();
            }
            catch
            {
                dto = new ConfirmSettings();
            }
        }
        bool prev = _suppressDirty;
        _suppressDirty = true;
        ConfirmExit         = dto.ConfirmExit;
        ConfirmHangup       = dto.ConfirmHangup;
        ConfirmSaveSettings = dto.ConfirmSaveSettings;
        ConfirmDeletes      = dto.ConfirmDeletes;
        _suppressDirty = prev;
    }

    // Persist the four Confirm* observables back into the Global tier and trigger
    // the live mirror via SettingsService.GlobalSettingsChanged.
    private void SaveConfirmToGlobalSettings()
    {
        ConfirmSettings dto = new()
        {
            ConfirmExit         = ConfirmExit,
            ConfirmHangup       = ConfirmHangup,
            ConfirmSaveSettings = ConfirmSaveSettings,
            ConfirmDeletes      = ConfirmDeletes,
        };
        _globalSettings.Current.Settings ??= new Dictionary<string, System.Text.Json.JsonElement>();
        _globalSettings.Current.Settings["Confirm"] =
            System.Text.Json.JsonSerializer.SerializeToElement(dto);
        _globalSettings.Save();
    }

    // Commit the selected BBS's per-character credentials onto the loaded
    // profile. Selecting a BBS here is now editing-only — it NEVER re-homes /
    // pins the loaded character (that moved to the Profile Management window's
    // explicit "Assign to BBS"). So this just writes the credential slice for
    // whichever BBS the user is editing; the profile stays where it lives.
    private void ApplyToCurrentProfile()
    {
        if (_profile.Current is not { } character) return;

        // Fold the board on screen into the staging map so it commits alongside
        // the ones already staged. Unconditional (not gated on _credentialsTouched)
        // because an untouched selection still persists the starter sys-goto set —
        // see the seed in LoadCredentialsFor.
        if (SelectedBbsName is { } selected) StageCredentials(selected);
        if (_stagedCredentials.Count == 0) return;

        foreach ((string bbs, StagedCredentials staged) in _stagedCredentials)
            WriteCredentials(bbs, character, staged);

        // Save() no-ops on drafts (no name to write to). NotifyMutated always
        // fires so observers refresh either way. NotifyBbsPinApplied is NOT
        // fired here: editing a BBS's credentials no longer changes the active-BBS
        // identity (re-home moved to Profile Management), so a credential edit
        // warrants only a mutation signal — BbsPinApplied now fires solely on a
        // real active-BBS change.
        _profile.Save();
        _profile.NotifyMutated();
    }

    // Write one board's staged credential slice (username, password, menu-nav,
    // sysop flags) onto the loaded profile. Runs whenever any CharacterProfile is
    // loaded (draft or named) because the inline EncryptedPassword is keyed off
    // the per-user .credkey, not the profile name — a draft's BbsCredentials
    // survive into its first Save. Persisting is the caller's job: one Save covers
    // every board committed in the same Apply.
    private void WriteCredentials(string bbs, CharacterProfile character, StagedCredentials staged)
    {
        // Case-insensitive: BBS names are folder names on a case-insensitive
        // FS, so a 'Playpen' credential must resolve for a 'playpen' BBS.
        character.BbsCredentials ??= new(StringComparer.OrdinalIgnoreCase);
        if (!character.BbsCredentials.TryGetValue(bbs, out BbsCredentials? cred))
        {
            cred = new BbsCredentials();
            character.BbsCredentials[bbs] = cred;
        }
        cred.EncryptedUsername = string.IsNullOrEmpty(staged.Username)
            ? null
            : _passwords.Protect(staged.Username);
        cred.MenuNavSteps = staged.MenuNavSteps;
        cred.SysopStatus = staged.SysopStatus;
        cred.SysopGodLives = staged.SysopGodLives;
        cred.SysopGoto = staged.SysopGoto;
        cred.SysopGotos = staged.SysopGotos;

        if (staged.PasswordTouched) cred.EncryptedPassword = staged.EncryptedPassword;
    }

    private void RenameSelected(string oldName, string newName)
    {
        if (!_loaded.TryGetValue(oldName, out BbsProfile? profile))
        {
            profile = _bbsStore.Get(oldName);
            if (profile is null) return;
        }

        // Don't trample an existing BBS with the new name. Test the folder, not
        // just a loadable bbs.json: BbsProfileStore.Rename moves the whole folder
        // and Directory.Move throws if the destination folder exists at all —
        // even a stray one with no bbs.json (half-deleted BBS, or one holding
        // only nested profiles). Guarding on Get(newName) alone let those slip
        // through and crashed the app with an unhandled IOException on Apply.
        if (_loaded.ContainsKey(newName) || _bbsStore.Exists(newName))
        {
            AppServices.Current.Log.Info("BBS",
                $"Rename '{oldName}' → '{newName}' refused: a folder for that name already exists.");
            AppServices.Current.Dialogs.ShowInfo(
                "BBS not renamed",
                $"A BBS named “{newName}” already exists. Pick a name that isn't in use.");
            return;
        }

        // Move the whole Data/BBS/{old}/ subtree — bbs.json, side-files, and
        // every nested character profile — to the new name. The old
        // Delete+Save pair recursively destroyed the nested profiles and left
        // every reference to the BBS name (credentials, recent list) dangling.
        _bbsStore.Rename(oldName, newName);
        profile.Name = newName;
        _loaded.Remove(oldName);
        _loaded[newName] = profile;
        // ProfileService.RenameBbs re-keys the stored credentials below; carry
        // any *staged* ones across too, or an unsaved login edit would commit
        // under the name the board no longer has.
        if (_stagedCredentials.Remove(oldName, out StagedCredentials? stagedCred))
            _stagedCredentials[newName] = stagedCred;

        // The BBS name keys per-character credentials and the recent-profiles
        // refs — cascade the rename so logon-nav / passwords, the File → Recent
        // menu, and the "import logon steps" picker follow the new name.
        _profile.RenameBbs(oldName, newName);
        RecentProfileList.RekeyBbs(_globalSettings, oldName, newName);

        _suppressDirty = true;
        ReloadBbsList();
        SelectedBbsName = newName;
        _suppressDirty = false;
    }

    public override void Discard()
    {
        // Drop every cached in-memory edit and re-fetch from disk on the
        // next selection. Keeps the Apply contract: Cancel really cancels.
        _loaded.Clear();
        ResetCredentialStaging();
        if (SelectedBbsName is not null)
        {
            _suppressDirty = true;
            ReloadSelected();
            _suppressDirty = false;
        }

        // Roll Confirm* observables back to their on-disk values too —
        // they're independent of the BBS cache but share this section's
        // dirty bit.
        LoadConfirmFromGlobalSettings();

        // Roll the live DisplayConfig back to the *active* BBS, not the
        // BBS that happened to be selected in the editor. Otherwise the
        // terminal canvas keeps the discarded preview font.
        SyncDisplayToActiveBbs();
        ClearDirty();
    }

    private void SyncDisplayToActiveBbs()
    {
        string? activeName = _profile.CurrentBbsName;
        BbsProfile? active = string.IsNullOrEmpty(activeName) ? null : _bbsStore.Get(activeName);
        BbsProfile values = active ?? new BbsProfile();
        _display.ScrollbackLines = values.ScrollbackLines;
        _display.BackscrollWheelLines = values.BackscrollWheelLines;
        _display.TerminalCols = values.TerminalCols;
        _display.TerminalRows = values.TerminalRows;
    }

    // Adding / removing BBSes moved to the Profile Management window (View →
    // Profile Management); this tab now only selects a saved BBS to edit its
    // fields. Renaming still happens here via the Name field on Apply. The
    // "Open Profile Management" button routes through the AppServices bridge
    // (the window is owned by the main VM).
    [RelayCommand]
    private void OpenProfileManager() => AppServices.Current.OpenProfileManager();

    // Capture the outgoing board's credential edits before the incoming one
    // overwrites the fields. This is the only transition that can lose them, so
    // it's the only place that has to stage — cheaper than mirroring PushToCache
    // across every credential control and the menu-step / sys-goto row VMs.
    partial void OnSelectedBbsNameChanging(string? oldValue, string? newValue)
    {
        if (_suppressDirty || !_credentialsTouched) return;
        if (oldValue is { } previous) StageCredentials(previous);
    }

    partial void OnSelectedBbsNameChanged(string? value)
    {
        if (_suppressDirty) return;
        _suppressDirty = true;
        ReloadSelected();
        _suppressDirty = false;
    }

    // Snapshot the credential block for one board into the staging map.
    private void StageCredentials(string bbsName)
    {
        if (!HasProfile) return;
        _stagedCredentials.TryGetValue(bbsName, out StagedCredentials? previous);

        StagedCredentials staged = new()
        {
            Username = Username,
            SysopStatus = SysopStatus,
            SysopGodLives = SysopGodLives,
            SysopGoto = SysopGoto,
            MenuNavSteps = MenuNavSteps.Select(vm => vm.ToModel()).ToList(),
            SysopGotos = SysopGotos.Select(vm => vm.ToModel()).ToList(),
        };

        if (_pendingPassword is not null)
        {
            staged.PasswordTouched = true;
            staged.EncryptedPassword = _pendingPassword.Length == 0
                ? null
                : _passwords.Protect(_pendingPassword);
        }
        else if (previous is not null)
        {
            // Re-staging a board the user came back to but didn't retype the
            // password on — the box shows empty by design, so an earlier
            // password edit would read as "untouched" and be dropped here.
            staged.PasswordTouched = previous.PasswordTouched;
            staged.EncryptedPassword = previous.EncryptedPassword;
        }

        _stagedCredentials[bbsName] = staged;
    }

    private void ReloadBbsList()
    {
        AvailableBbsNames.Clear();
        foreach (string name in _bbsStore.ListNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            AvailableBbsNames.Add(name);
        }
    }

    private void ReloadSelected()
    {
        if (SelectedBbsName is not { } name)
        {
            ResetFields();
            return;
        }

        if (!_loaded.TryGetValue(name, out BbsProfile? profile))
        {
            profile = _bbsStore.Get(name) ?? new BbsProfile { Name = name };
            _loaded[name] = profile;
        }

        Name = profile.Name;
        Host = profile.Host;

        LoadCredentialsFor(name);
        Port = profile.Port;
        MaxRedials = profile.MaxRedials;
        RedialPauseSeconds = profile.RedialPauseSeconds;
        InfiniteRetries = profile.InfiniteRetries;
        CleanupPeriodMinutes = profile.CleanupPeriodMinutes;
        NoResponseTimeoutSeconds = profile.NoResponseTimeoutSeconds;
        ReconnectOnFailedConnect = profile.ReconnectOnFailedConnect;
        ReconnectOnCarrierLost = profile.ReconnectOnCarrierLost;
        ReconnectOnNoResponse = profile.ReconnectOnNoResponse;
        ReconnectAfterCleanup = profile.ReconnectAfterCleanup;
        TerminalCols = profile.TerminalCols;
        TerminalRows = profile.TerminalRows;
        ScrollbackLines = profile.ScrollbackLines;
        BackscrollWheelLines = profile.BackscrollWheelLines;
        GameEntryCommand = profile.GameEntryCommand;
        GameExitCommand = profile.GameExitCommand;
        PlayerDiesAtHp = profile.PlayerDiesAtHp;
        AutoRefineDeathFloor = profile.AutoRefineDeathFloor;
        CleanupTimeOfDay = profile.CleanupTimeOfDay;
        CleanupTimeZoneId = profile.CleanupTimeZoneId;
        DisconnectPattern = profile.DisconnectPattern;
        RunicCurrencyName = profile.RunicCurrencyName;
    }

    private void LoadCredentialsFor(string bbsName)
    {
        _pendingPassword = null;
        _credentialsTouched = false;
        MenuNavSteps.Clear();
        SysopGotos.Clear();
        if (!HasProfile)
        {
            Username = string.Empty;
            Password = string.Empty;
            SysopStatus = SysopGodLives = SysopGoto = false;
            return;
        }

        // Pending edits win over what's on the profile — same contract _loaded
        // gives the BBS-profile fields, so clicking back and forth between two
        // boards doesn't reset either one's half-finished login.
        if (_stagedCredentials.TryGetValue(bbsName, out StagedCredentials? staged))
        {
            Username = staged.Username;
            Password = string.Empty;
            SysopStatus = staged.SysopStatus;
            SysopGodLives = staged.SysopGodLives;
            SysopGoto = staged.SysopGoto;
            foreach (MenuStep step in staged.MenuNavSteps)
                MenuNavSteps.Add(MenuStepEditorViewModel.FromModel(step, CredentialsDirty));
            foreach (SysopGotoLocation loc in staged.SysopGotos)
                SysopGotos.Add(SysopGotoRowViewModel.FromModel(loc, CredentialsDirty, RoomName));
            RefreshImportSources(bbsName);
            return;
        }

        CharacterProfile? character = _profile.Current;
        if (character?.BbsCredentials is not null
            && character.BbsCredentials.TryGetValue(bbsName, out BbsCredentials? cred))
        {
            // Username is encrypted at rest; decrypted for the UI
            // because the doc shows it plainly.
            Username = cred.EncryptedUsername is { } enc
                ? (_passwords.Unprotect(enc) ?? string.Empty)
                : string.Empty;
            // Password isn't pulled from the credential store eagerly — that
            // would surface the plaintext over a logging boundary every time
            // the user clicks around. Show empty + a placeholder; typing a
            // new one replaces, leaving it empty preserves the existing.
            Password = string.Empty;
            SysopStatus = cred.SysopStatus;
            SysopGodLives = cred.SysopGodLives;
            SysopGoto = cred.SysopGoto;
            foreach (MenuStep step in cred.MenuNavSteps)
            {
                MenuNavSteps.Add(MenuStepEditorViewModel.FromModel(step, CredentialsDirty));
            }
            foreach (SysopGotoLocation loc in cred.SysopGotos)
            {
                SysopGotos.Add(SysopGotoRowViewModel.FromModel(loc, CredentialsDirty, RoomName));
            }
        }
        else
        {
            Username = string.Empty;
            Password = string.Empty;
            SysopStatus = SysopGodLives = SysopGoto = false;
            // A BBS with no credential yet still shows the starter goto locations, so
            // saving it persists them instead of an empty table — CommitCredentials
            // writes this collection wholesale over the model's own starter default,
            // so the seed has to live here too (mirrors BbsCredentials.SysopGotos).
            foreach (SysopGotoLocation loc in SysopGotoLocation.DefaultStarterSet())
            {
                SysopGotos.Add(SysopGotoRowViewModel.FromModel(loc, CredentialsDirty, RoomName));
            }
        }

        RefreshImportSources(bbsName);
    }

    // Load every other saved character's logon steps into the import picker. Runs
    // on each BBS-select / profile-load (the editing target drives which pair is
    // excluded). Reads profiles straight from disk without switching to them, so a
    // corrupt one is skipped rather than aborting the whole list.
    private void RefreshImportSources(string editingBbs)
    {
        ImportSourceOptions.Clear();
        SelectedImportSource = null;

        if (HasProfile)
        {
            var loaded = new List<(string bbs, string name, CharacterProfile profile)>();
            foreach (ProfileRef r in _profile.ListAll())
            {
                CharacterProfile? p;
                try
                {
                    p = JsonStore.Load<CharacterProfile>(AppPaths.CharacterProfileFile(r.Bbs, r.Name));
                }
                catch (InvalidDataException)
                {
                    // One unreadable profile shouldn't blank the picker for the rest.
                    continue;
                }
                if (p is not null) loaded.Add((r.Bbs, r.Name, p));
            }

            foreach (MenuNavImportOption option in MenuNavImportOption.Build(
                         loaded, editingBbs, _profile.CurrentBbsName, _profile.CurrentProfileName))
                ImportSourceOptions.Add(option);
        }

        OnPropertyChanged(nameof(HasImportSources));
    }

    private void RefreshProfileState()
    {
        // Runs on profile load / close. Credentials are per-character, so a swap
        // while the window is open invalidates everything staged — committing it
        // would write the outgoing character's logins onto the incoming one.
        ResetCredentialStaging();
        HasProfile = _profile.Current is not null;
        OnPropertyChanged(nameof(CredentialsHint));
        OnPropertyChanged(nameof(IsCredentialsHintWarning));
        if (SelectedBbsName is not null)
        {
            _suppressDirty = true;
            LoadCredentialsFor(SelectedBbsName);
            _suppressDirty = false;
        }
        RefreshSuicidePassword();
    }

    // Hydrate SuicidePassword from the loaded profile's encrypted blob. Runs on
    // every profile load / mutate / close so the field reflects the live state —
    // including the wipe case where Game.SuicidePasswordTracker saw `pro`'s "You do
    // not have a suicide password set." line and cleared the stored value.
    private void RefreshSuicidePassword()
    {
        string decrypted = string.Empty;
        if (_profile.Current is { } profile
            && profile.EncryptedSuicidePassword is { Length: > 0 } blob)
        {
            decrypted = _passwords.Unprotect(blob) ?? string.Empty;
        }
        _suppressDirty = true;
        SuicidePassword = decrypted;
        _suppressDirty = false;
    }

    private void ResetFields()
    {
        BbsProfile defaults = new();
        Name = defaults.Name;
        Host = defaults.Host;
        Port = defaults.Port;
        MaxRedials = defaults.MaxRedials;
        RedialPauseSeconds = defaults.RedialPauseSeconds;
        InfiniteRetries = defaults.InfiniteRetries;
        CleanupPeriodMinutes = defaults.CleanupPeriodMinutes;
        NoResponseTimeoutSeconds = defaults.NoResponseTimeoutSeconds;
        ReconnectOnFailedConnect = defaults.ReconnectOnFailedConnect;
        ReconnectOnCarrierLost = defaults.ReconnectOnCarrierLost;
        ReconnectOnNoResponse = defaults.ReconnectOnNoResponse;
        ReconnectAfterCleanup = defaults.ReconnectAfterCleanup;
        TerminalCols = defaults.TerminalCols;
        TerminalRows = defaults.TerminalRows;
        ScrollbackLines = defaults.ScrollbackLines;
        BackscrollWheelLines = defaults.BackscrollWheelLines;
        GameEntryCommand = defaults.GameEntryCommand;
        GameExitCommand = defaults.GameExitCommand;
        PlayerDiesAtHp = defaults.PlayerDiesAtHp;
        AutoRefineDeathFloor = defaults.AutoRefineDeathFloor;
        CleanupTimeOfDay = defaults.CleanupTimeOfDay;
        CleanupTimeZoneId = defaults.CleanupTimeZoneId;
        DisconnectPattern = defaults.DisconnectPattern;
        RunicCurrencyName = defaults.RunicCurrencyName;
    }

    private void Dirty()
    {
        if (_suppressDirty || _dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void ClearDirty()
    {
        if (!_dirty) return;
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    // Dirty() for the credential block. The extra flag arms the staging pass on
    // the next board switch, so untouched boards the user merely clicked through
    // don't get a credential entry authored for them.
    private void CredentialsDirty()
    {
        if (_suppressDirty) return;
        _credentialsTouched = true;
        Dirty();
    }

    // Field-change hooks: writes the new value into the in-memory cache for
    // the currently-selected BBS so Apply has something fresh to persist.
    private void PushToCache()
    {
        if (_suppressDirty) return;
        if (SelectedBbsName is not { } name) return;
        if (!_loaded.TryGetValue(name, out BbsProfile? profile)) return;

        profile.Host = Host;
        profile.Port = Port;
        profile.MaxRedials = MaxRedials;
        profile.RedialPauseSeconds = RedialPauseSeconds;
        profile.InfiniteRetries = InfiniteRetries;
        profile.CleanupPeriodMinutes = CleanupPeriodMinutes;
        profile.NoResponseTimeoutSeconds = NoResponseTimeoutSeconds;
        profile.ReconnectOnFailedConnect = ReconnectOnFailedConnect;
        profile.ReconnectOnCarrierLost = ReconnectOnCarrierLost;
        profile.ReconnectOnNoResponse = ReconnectOnNoResponse;
        profile.ReconnectAfterCleanup = ReconnectAfterCleanup;
        profile.TerminalCols = TerminalCols;
        profile.TerminalRows = TerminalRows;
        profile.ScrollbackLines = ScrollbackLines;
        profile.BackscrollWheelLines = BackscrollWheelLines;
        profile.GameEntryCommand = string.IsNullOrWhiteSpace(GameEntryCommand)
            ? new BbsProfile().GameEntryCommand : GameEntryCommand.Trim();
        profile.GameExitCommand = string.IsNullOrWhiteSpace(GameExitCommand)
            ? new BbsProfile().GameExitCommand : GameExitCommand.Trim();
        // Death floor is a negative-HP value; a positive entry is meaningless
        // (0 HP already means dropped), so clamp to <= 0 at the point of storage.
        profile.PlayerDiesAtHp = Math.Min(0, PlayerDiesAtHp);
        profile.AutoRefineDeathFloor = AutoRefineDeathFloor;
        profile.CleanupTimeOfDay = CleanupTimeOfDay?.Trim() ?? string.Empty;
        profile.CleanupTimeZoneId = string.IsNullOrWhiteSpace(CleanupTimeZoneId)
            ? new BbsProfile().CleanupTimeZoneId : CleanupTimeZoneId.Trim();
        profile.DisconnectPattern = string.IsNullOrWhiteSpace(DisconnectPattern)
            ? null : DisconnectPattern.Trim();
        profile.RunicCurrencyName = string.IsNullOrWhiteSpace(RunicCurrencyName)
            ? new BbsProfile().RunicCurrencyName : RunicCurrencyName.Trim();
    }

    partial void OnNameChanged(string value)                    { Dirty(); }
    partial void OnUsernameChanged(string value)                { CredentialsDirty(); }
    partial void OnPasswordChanged(string value)
    {
        if (_suppressDirty) return;
        _pendingPassword = value;
        CredentialsDirty();
    }

    // Toggling Show ON pulls the stored password out of the credential store on
    // demand — so users can verify what's saved without leaking the plaintext
    // through the UI on every Settings open. Toggling OFF leaves the box as-is (the
    // user may have started editing); if they haven't touched it, _pendingPassword
    // stays null and the Apply path no-ops the credential store.
    partial void OnShowPasswordChanged(bool value)
    {
        if (!value) return;
        if (!HasProfile) return;
        if (!string.IsNullOrEmpty(Password)) return;
        if (_pendingPassword is not null) return;
        if (SelectedBbsName is not { } bbs) return;

        // A password edited earlier in this window sits in the staging map, not
        // on the profile — reveal what OK would write, not what's on disk. A
        // staged clear (touched with no blob) correctly reveals nothing.
        string? blob;
        if (_stagedCredentials.TryGetValue(bbs, out StagedCredentials? staged) && staged.PasswordTouched)
        {
            blob = staged.EncryptedPassword;
        }
        else
        {
            CharacterProfile? character = _profile.Current;
            if (character?.BbsCredentials is null) return;
            if (!character.BbsCredentials.TryGetValue(bbs, out BbsCredentials? cred)) return;
            blob = cred.EncryptedPassword;
        }
        if (blob is null) return;

        string? pw = _passwords.Unprotect(blob);
        if (string.IsNullOrEmpty(pw)) return;

        // Suppress the OnPasswordChanged side-effect: this assignment is a
        // reveal, not a user edit. Without the gate, _pendingPassword would
        // get stamped with the same value and Apply would re-encrypt it
        // back to the profile as if the user had retyped it.
        _suppressDirty = true;
        try { Password = pw; }
        finally { _suppressDirty = false; }
    }
    partial void OnHostChanged(string value)                    { PushToCache(); Dirty(); }
    partial void OnPortChanged(int value)                       { PushToCache(); Dirty(); }
    partial void OnMaxRedialsChanged(int value)                 { PushToCache(); Dirty(); }
    partial void OnRedialPauseSecondsChanged(int value)         { PushToCache(); Dirty(); }
    partial void OnInfiniteRetriesChanged(bool value)           { OnPropertyChanged(nameof(RedialTickersEnabled)); PushToCache(); Dirty(); }
    partial void OnCleanupPeriodMinutesChanged(int value)       { PushToCache(); Dirty(); }
    partial void OnNoResponseTimeoutSecondsChanged(int value)   { PushToCache(); Dirty(); }
    partial void OnReconnectOnFailedConnectChanged(bool value)  { PushToCache(); Dirty(); }
    partial void OnReconnectOnCarrierLostChanged(bool value)    { PushToCache(); Dirty(); }
    partial void OnReconnectOnNoResponseChanged(bool value)     { PushToCache(); Dirty(); }
    partial void OnReconnectAfterCleanupChanged(bool value)     { PushToCache(); Dirty(); }
    partial void OnSysopStatusChanged(bool value)               { CredentialsDirty(); }
    partial void OnSysopGodLivesChanged(bool value)             { CredentialsDirty(); }
    partial void OnSysopGotoChanged(bool value)                 { CredentialsDirty(); }
    partial void OnTerminalColsChanged(int value)               { PushToCache(); Dirty(); }
    partial void OnTerminalRowsChanged(int value)               { PushToCache(); Dirty(); }

    partial void OnScrollbackLinesChanged(int value)            { PushToCache(); Dirty(); }
    partial void OnBackscrollWheelLinesChanged(int value)       { PushToCache(); Dirty(); }
    partial void OnGameEntryCommandChanged(string value)        { PushToCache(); Dirty(); }
    partial void OnGameExitCommandChanged(string value)         { PushToCache(); Dirty(); }
    partial void OnPlayerDiesAtHpChanged(int value)             { PushToCache(); Dirty(); }
    partial void OnAutoRefineDeathFloorChanged(bool value)      { PushToCache(); Dirty(); }
    partial void OnCleanupTimeOfDayChanged(string value)        { PushToCache(); Dirty(); }
    partial void OnCleanupTimeZoneIdChanged(string value)       { PushToCache(); Dirty(); }
    partial void OnDisconnectPatternChanged(string? value)      { PushToCache(); Dirty(); }
    partial void OnRunicCurrencyNameChanged(string value)       { PushToCache(); Dirty(); }

    // Confirm flags are Global-tier, not per-BBS — they don't push into
    // the per-BBS cache, just mark the section dirty so Apply commits
    // them via SaveConfirmToGlobalSettings.
    partial void OnConfirmExitChanged(bool value)               { Dirty(); }
    partial void OnConfirmHangupChanged(bool value)             { Dirty(); }
    partial void OnConfirmSaveSettingsChanged(bool value)       { Dirty(); }
    partial void OnConfirmDeletesChanged(bool value)            { Dirty(); }

    [RelayCommand]
    private void AddMenuStep()
    {
        if (_suppressDirty) return;
        MenuNavSteps.Add(new MenuStepEditorViewModel(CredentialsDirty));
        CredentialsDirty();
    }

    [RelayCommand]
    private void AddSysopGoto()
    {
        if (_suppressDirty) return;
        SysopGotos.Add(new SysopGotoRowViewModel(CredentialsDirty, RoomName));
        CredentialsDirty();
    }

    [RelayCommand]
    private void RemoveSysopGoto(SysopGotoRowViewModel? row)
    {
        if (row is null || _suppressDirty) return;
        if (!SysopGotos.Remove(row)) return;
        CredentialsDirty();
    }

    // Resolve a landing room's name for a row's read-only preview cell. Reads the
    // live active-set graph; null-safe for headless / pre-init contexts (the
    // preview binding simply shows "(unknown room)").
    private static string? RoomName(int map, int room)
        => AppServices.Current is { } svc
            ? svc.RoomGraph.GetRoom(new Game.Map.RoomKey(map, room))?.Name
            : null;

    [RelayCommand]
    private void RemoveMenuStep(MenuStepEditorViewModel? step)
    {
        if (step is null) return;
        if (!MenuNavSteps.Remove(step)) return;
        CredentialsDirty();
    }

    [RelayCommand]
    private void MoveMenuStepUp(MenuStepEditorViewModel? step)
    {
        if (step is null) return;
        int i = MenuNavSteps.IndexOf(step);
        if (i <= 0) return;
        MenuNavSteps.Move(i, i - 1);
        CredentialsDirty();
    }

    [RelayCommand]
    private void MoveMenuStepDown(MenuStepEditorViewModel? step)
    {
        if (step is null) return;
        int i = MenuNavSteps.IndexOf(step);
        if (i < 0 || i >= MenuNavSteps.Count - 1) return;
        MenuNavSteps.Move(i, i + 1);
        CredentialsDirty();
    }

    // Replace the current character's logon steps with a copy of the chosen
    // source's. Destructive by design but recoverable: Settings is a Save/Cancel
    // window, so Cancel / X drops the import if it wasn't the right starting point.
    [RelayCommand(CanExecute = nameof(CanImportMenuNav))]
    private void ImportMenuNav()
    {
        if (SelectedImportSource is not { } src) return;
        MenuNavSteps.Clear();
        foreach (MenuStep step in src.Steps)
            MenuNavSteps.Add(MenuStepEditorViewModel.FromModel(step, CredentialsDirty));
        CredentialsDirty();
    }

    private bool CanImportMenuNav() => SelectedImportSource is not null;
}
