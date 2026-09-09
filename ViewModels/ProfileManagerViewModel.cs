using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Profile;

namespace MudPlay.ViewModels;

// Modeless Profile Management window VM — the one place to add / rename / delete
// / swap characters, assign a character to a BBS, and add / remove / rename
// BBSes. Structural ops commit immediately via ProfileService / BbsProfileStore
// (folder moves + deletes on disk), so the window needs no Save/Cancel staging.
// Mutating the LOADED character (swap / delete / rename / assign of the current
// profile, or removing the BBS it lives on) requires being disconnected; every
// other profile is freely mutable. Current-profile lifecycle (New / Save / Save
// As) is routed back to the MainWindowViewModel commands that used to live on
// the File menu, so the collapsed menu loses no capability.
public sealed partial class ProfileManagerViewModel : ObservableObject, IDisposable
{
    private readonly Func<bool> _isDisconnected;
    private readonly Action<ProfileRef> _swapToProfile;
    private readonly Action _newProfile;
    private readonly Action _saveCurrent;
    private readonly Func<Task> _saveCurrentAs;
    private readonly ProfileService _profile;
    private readonly BbsProfileStore _bbs;
    private readonly DialogService _dialogs;
    private readonly ConfirmService _confirm;
    private readonly SettingsService _settings;
    private readonly LogService? _log;
    private bool _disposed;

    public ObservableCollection<string> Bbses { get; } = new();
    public ObservableCollection<ProfileManagerRow> Profiles { get; } = new();

    // Every character ticked in the (multi-select) list — drives "Launch": one
    // new client instance is spawned per selected character.
    public ObservableCollection<ProfileManagerRow> SelectedProfiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBbsSelection))]
    [NotifyPropertyChangedFor(nameof(AssignableBbses))]
    private string? _selectedBbs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfileSelection))]
    private ProfileManagerRow? _selectedProfile;

    [ObservableProperty] private string? _assignTargetBbs;
    [ObservableProperty] private bool _isProfilesEmpty = true;
    [ObservableProperty] private string _currentProfileLabel = "No character loaded";

    public bool HasBbsSelection => SelectedBbs is not null;
    public bool HasProfileSelection => SelectedProfile is not null;

    // Destination choices for "Assign to BBS" — every saved BBS except the one
    // the selected character already lives under.
    public IEnumerable<string> AssignableBbses =>
        Bbses.Where(b => !string.Equals(b, SelectedBbs, StringComparison.OrdinalIgnoreCase));

    public ProfileManagerViewModel(
        Func<bool> isDisconnected,
        Action<ProfileRef> swapToProfile,
        Action newProfile,
        Action saveCurrent,
        Func<Task> saveCurrentAs)
    {
        _isDisconnected = isDisconnected;
        _swapToProfile = swapToProfile;
        _newProfile = newProfile;
        _saveCurrent = saveCurrent;
        _saveCurrentAs = saveCurrentAs;
        _profile = AppServices.Current.Profile;
        _bbs = AppServices.Current.Bbs;
        _dialogs = AppServices.Current.Dialogs;
        _confirm = AppServices.Current.Confirm;
        _settings = AppServices.Current.Settings;
        _log = AppServices.Current.Log;

        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileMutated += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedHandler;

        ReloadBbses();
        // Land on the loaded character's BBS so its record is selected on open.
        if (_profile.CurrentBbsName is { } cur)
            SelectedBbs = Bbses.FirstOrDefault(b => string.Equals(b, cur, StringComparison.OrdinalIgnoreCase))
                          ?? Bbses.FirstOrDefault();
        else
            SelectedBbs = Bbses.FirstOrDefault();
        RefreshCurrentLabel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _profile.ProfileLoaded -= OnProfileChanged;
        _profile.ProfileMutated -= OnProfileChanged;
        _profile.ProfileClosed -= OnProfileClosedHandler;
    }

    private void OnProfileChanged(CharacterProfile _) { RefreshCurrentLabel(); ReloadProfiles(); }
    private void OnProfileClosedHandler() { RefreshCurrentLabel(); ReloadProfiles(); }

    partial void OnSelectedBbsChanged(string? value) => ReloadProfiles();

    private void RefreshCurrentLabel() =>
        CurrentProfileLabel = _profile.Current is null
            ? "No character loaded"
            : _profile.CurrentProfileName is null
                ? "{default} — Use Save to update the default profile template"
                : $"{_profile.CurrentBbsName} / {_profile.CurrentProfileName}";

    private void ReloadBbses()
    {
        string? keep = SelectedBbs;
        Bbses.Clear();
        foreach (string name in _bbs.ListNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            Bbses.Add(name);
        OnPropertyChanged(nameof(AssignableBbses));
        if (keep is not null && Bbses.Contains(keep, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(SelectedBbs, keep, StringComparison.Ordinal)) SelectedBbs = keep;
        }
        else if (SelectedBbs is not null) SelectedBbs = Bbses.FirstOrDefault();
    }

    private void ReloadProfiles()
    {
        Profiles.Clear();
        if (SelectedBbs is { } bbs)
        {
            foreach (ProfileRef r in _profile.ListAll()
                         .Where(r => string.Equals(r.Bbs, bbs, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                bool isCurrent = string.Equals(_profile.CurrentBbsName, r.Bbs, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(_profile.CurrentProfileName, r.Name, StringComparison.Ordinal);
                Profiles.Add(new ProfileManagerRow(r, isCurrent));
            }
        }
        IsProfilesEmpty = Profiles.Count == 0;
    }

    // Refuse an op that would strand the live in-game session, pointing the user
    // at the fix. Returns true only when it's safe to proceed (disconnected).
    private bool EnsureDisconnected(string action)
    {
        if (_isDisconnected()) return true;
        _dialogs.ShowInfo("Disconnect first", $"Disconnect from the BBS before you {action}.");
        return false;
    }

    private string DeriveUniqueName(string bbs, string baseName)
    {
        if (!_profile.Exists(bbs, baseName)) return baseName;
        for (int n = 2; ; n++)
        {
            string candidate = $"{baseName} {n}";
            if (!_profile.Exists(bbs, candidate)) return candidate;
        }
    }

    // ----- BBS list -------------------------------------------------------

    [RelayCommand]
    private void AddBbs()
    {
        string baseName = "New BBS";
        string name = baseName;
        int n = 2;
        while (_bbs.Exists(name)) name = $"{baseName} {n++}";
        _bbs.Save(new BbsProfile { Name = name, Host = string.Empty, Port = 23 });
        _log?.Info("BBS", $"Added BBS '{name}'.");
        ReloadBbses();
        SelectedBbs = name;
    }

    [RelayCommand]
    private async Task RemoveBbsAsync()
    {
        if (SelectedBbs is not { } name) return;
        int count = _profile.ListAll().Count(r => string.Equals(r.Bbs, name, StringComparison.OrdinalIgnoreCase));
        string scope = count == 0
            ? $"the BBS '{name}'"
            : $"the BBS '{name}' and all {count} character{(count == 1 ? "" : "s")} saved under it";
        bool deletingCurrent = _profile.CurrentProfileName is not null
            && string.Equals(_profile.CurrentBbsName, name, StringComparison.OrdinalIgnoreCase);
        if (deletingCurrent && !EnsureDisconnected("remove the BBS your loaded character lives on")) return;
        if (!await _confirm.ConfirmDeleteAsync(scope)) return;

        if (deletingCurrent) _profile.Close();   // no save — else the outgoing save resurrects the folder
        _bbs.Delete(name);
        _log?.Info("BBS", $"Removed BBS '{name}'.");
        if (deletingCurrent) _profile.LoadDefaultProfile();
        ReloadBbses();
        SelectedBbs = Bbses.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RenameBbsAsync()
    {
        if (SelectedBbs is not { } oldName) return;
        ProfileNameInputDialogViewModel vm = new(
            oldName,
            n => !string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase) && _bbs.Exists(n));
        string? newName = await _dialogs.OpenWindowAsync<ProfileNameInputDialogViewModel, string>(vm);
        if (string.IsNullOrWhiteSpace(newName)
            || string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase)) return;
        if (_bbs.Exists(newName))
        {
            _dialogs.ShowInfo("BBS not renamed", $"A BBS named “{newName}” already exists.");
            return;
        }
        _bbs.Rename(oldName, newName);
        _profile.RenameBbs(oldName, newName);
        RecentProfileList.RekeyBbs(_settings, oldName, newName);
        _log?.Info("BBS", $"Renamed BBS '{oldName}' → '{newName}'.");
        ReloadBbses();
        SelectedBbs = newName;
    }

    // ----- Characters -----------------------------------------------------

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        if (SelectedBbs is not { } bbs) return;
        ProfileNameInputDialogViewModel vm = new("character", n => _profile.Exists(bbs, n));
        string? name = await _dialogs.OpenWindowAsync<ProfileNameInputDialogViewModel, string>(vm);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_profile.Exists(bbs, name))
        {
            _dialogs.ShowInfo("Character not created",
                $"A character named “{name}” already exists on “{bbs}”.");
            return;
        }
        _profile.CreateProfile(bbs, name);
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
    }

    [RelayCommand]
    private async Task RenameProfileAsync()
    {
        if (SelectedBbs is not { } bbs || SelectedProfile is not { } row) return;
        string oldName = row.Ref.Name;
        if (row.IsCurrent && !EnsureDisconnected("rename the loaded character")) return;
        ProfileNameInputDialogViewModel vm = new(
            oldName,
            n => !string.Equals(n, oldName, StringComparison.Ordinal) && _profile.Exists(bbs, n));
        string? newName = await _dialogs.OpenWindowAsync<ProfileNameInputDialogViewModel, string>(vm);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, oldName, StringComparison.Ordinal)) return;
        if (_profile.Exists(bbs, newName))
        {
            _dialogs.ShowInfo("Character not renamed",
                $"A character named “{newName}” already exists on “{bbs}”.");
            return;
        }
        bool wasCurrent = row.IsCurrent;
        _profile.MoveProfile(bbs, oldName, bbs, newName);
        if (wasCurrent) _profile.NotifyMutated();   // title / bindings refresh (same-BBS rename)
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(r => string.Equals(r.Name, newName, StringComparison.Ordinal));
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedBbs is not { } bbs || SelectedProfile is not { } row) return;
        if (row.IsCurrent && !EnsureDisconnected("delete the loaded character")) return;
        if (!await _confirm.ConfirmDeleteAsync($"the character '{row.Ref.Name}' on '{bbs}'")) return;
        _profile.DeleteProfile(bbs, row.Ref.Name);   // deleting current reloads the default (fires events → refresh)
        ReloadProfiles();
    }

    // The list is multi-select. Load brings the whole selection online, using
    // this client when it's free and spawning new instances (the --profile
    // multi-instance path) for the rest — so ticking four characters and
    // hitting Load leaves you with four running clients, one per character.
    //   • This client is DISCONNECTED (idle) → reuse it for the FIRST selection
    //     (a swap in place); each of the rest opens in its own new client.
    //   • This client is CONNECTED (actively playing) → don't disturb it: every
    //     selection opens in its own new client, instead of the jarring
    //     disconnect → swap → reconnect the old flow would force.
    [RelayCommand]
    private void Load()
    {
        List<ProfileManagerRow> targets = SelectedProfiles.Count > 0
            ? SelectedProfiles.OrderBy(r => Profiles.IndexOf(r)).ToList()
            : SelectedProfile is { } one ? new List<ProfileManagerRow> { one } : new();
        if (targets.Count == 0) return;

        if (!_isDisconnected())
        {
            LaunchNewClients(targets);   // playing → leave this client be
            return;
        }

        // Idle client: the first selection loads here (already loaded → no-op),
        // the rest open new.
        ProfileManagerRow first = targets[0];
        if (!first.IsCurrent) _swapToProfile(first.Ref);
        if (targets.Count > 1) LaunchNewClients(targets.Skip(1).ToList());
    }

    private void LaunchNewClients(IReadOnlyList<ProfileManagerRow> targets)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            _dialogs.ShowInfo("Load", "Couldn't locate the MudPlay executable to launch new clients.");
            return;
        }

        int launched = 0;
        foreach (ProfileManagerRow row in targets)
        {
            try
            {
                ProcessStartInfo psi = new(exe) { UseShellExecute = false };
                psi.ArgumentList.Add("--profile");
                psi.ArgumentList.Add($"{row.Ref.Bbs}/{row.Ref.Name}");
                Process.Start(psi);
                launched++;
            }
            catch (Exception ex)
            {
                _log?.Warn("Profile",
                    $"Failed to launch '{row.Ref.Name}' on '{row.Ref.Bbs}': {ex.Message}");
            }
        }
        if (launched > 0)
            _log?.Info("Profile", $"Launched {launched} client(s) from Profile Management.");
    }

    [RelayCommand]
    private async Task AssignToBbsAsync()
    {
        if (SelectedBbs is not { } fromBbs || SelectedProfile is not { } row) return;
        if (AssignTargetBbs is not { } toBbs || string.Equals(fromBbs, toBbs, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ShowInfo("Assign to BBS", "Pick a different destination BBS.");
            return;
        }
        if (row.IsCurrent && !EnsureDisconnected("move the loaded character to another BBS")) return;

        string name = row.Ref.Name;
        string targetName = name;
        if (_profile.Exists(toBbs, targetName))
        {
            // Name clash on the destination — prompt for a fresh name.
            ProfileNameInputDialogViewModel vm = new(DeriveUniqueName(toBbs, name), n => _profile.Exists(toBbs, n));
            string? chosen = await _dialogs.OpenWindowAsync<ProfileNameInputDialogViewModel, string>(vm);
            if (string.IsNullOrWhiteSpace(chosen)) return;
            if (_profile.Exists(toBbs, chosen))
            {
                _dialogs.ShowInfo("Not moved",
                    $"A character named “{chosen}” already exists on “{toBbs}”.");
                return;
            }
            targetName = chosen;
        }

        bool wasCurrent = row.IsCurrent;
        _profile.MoveProfile(fromBbs, name, toBbs, targetName);
        if (wasCurrent) { _profile.NotifyMutated(); _profile.NotifyBbsPinApplied(); }   // active BBS changed
        _log?.Info("Profile", targetName == name
            ? $"Assigned '{name}' to BBS '{toBbs}'."
            : $"Assigned '{name}' to BBS '{toBbs}' as '{targetName}'.");
        ReloadProfiles();
    }

    // ----- Current-profile lifecycle (the collapsed File-menu actions) ----

    [RelayCommand]
    private void NewCurrent()
    {
        if (!EnsureDisconnected("start a new character")) return;
        _newProfile();
        RefreshAfterCurrentChange();
    }

    [RelayCommand]
    private void SaveCurrent()
    {
        _saveCurrent();
        RefreshCurrentLabel();
    }

    [RelayCommand]
    private async Task SaveCurrentAsAsync()
    {
        await _saveCurrentAs();
        RefreshAfterCurrentChange();
    }

    private void RefreshAfterCurrentChange()
    {
        ReloadBbses();
        ReloadProfiles();
        RefreshCurrentLabel();
    }
}
