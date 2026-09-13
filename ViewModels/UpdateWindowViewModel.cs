using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;
using MudPlay.Services.Update;

namespace MudPlay.ViewModels;

// Backs Help / Tools → Update the Client. Reads the UpdateService's cached verdict (or
// kicks a fresh check when none exists) and — only on the user's explicit
// request — downloads, verifies, swaps the new build in, and relaunches. The
// window is modeless; the only state it owns is transient UI (progress, button
// enablement), so there's nothing to persist and no Save/Cancel contract.
public sealed partial class UpdateWindowViewModel : ObservableObject
{
    private readonly UpdateService _update;
    private readonly Func<Task<UpdateRelaunch>> _prepareExit;
    private readonly Action _exit;
    private readonly CancellationTokenSource _cts = new();
    private string? _releaseUrl;

    [ObservableProperty] private string _headline = "Checking for updates…";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isBusy;            // a check or download is in flight
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _canApply;          // show Download & Restart
    [ObservableProperty] private bool _hasReleaseLink;
    [ObservableProperty] private string _releaseNotes = "";
    [ObservableProperty] private bool _hasReleaseNotes;

    public string CurrentVersionLine => $"Installed version: {AppInfo.Version}";

    // prepareExit stands the live session down (close the connection, report what the
    // relaunch has to restore); exit closes the app. Both come from the main window's
    // view-model — this window doesn't own the session and shouldn't reach for it.
    public UpdateWindowViewModel(UpdateService update, Func<Task<UpdateRelaunch>> prepareExit, Action exit)
    {
        _update = update;
        _prepareExit = prepareExit;
        _exit = exit;
        // Show the cached verdict instantly when we have a usable one; otherwise
        // (never checked, or the last check errored) run a fresh check on open.
        if (_update.Last is { } cached && cached.State != UpdateAvailability.Error)
            Render(cached);
        else
            _ = CheckAsync();
    }

    // Re-query GitHub. Bound to the "Check again" button and run once on open when
    // there's no usable cached verdict.
    [RelayCommand]
    private async Task CheckAsync()
    {
        IsBusy = true;
        IsDownloading = false;
        CanApply = false;
        HasReleaseLink = false;
        HasReleaseNotes = false;
        Headline = "Checking for updates…";
        Detail = "";
        UpdateCheckResult r = await _update.CheckAsync(_cts.Token);
        Render(r);
        IsBusy = false;
    }

    // Download + verify + swap-in + relaunch. On success the swap helper is
    // detached and the app is asked to exit, so this window goes with it; on
    // failure the install is untouched and we surface the reason + let them retry.
    [RelayCommand]
    private async Task DownloadAndRestartAsync()
    {
        if (_update.Last is not { UpdateAvailable: true } r) return;
        IsBusy = true;
        IsDownloading = true;
        CanApply = false;
        Headline = "Downloading update…";
        Detail = "MudPlay will verify the download, replace this build, and restart automatically.";
        DownloadProgress = 0;
        ProgressText = "0%";

        var progress = new Progress<double>(p =>
        {
            DownloadProgress = p;
            ProgressText = $"{p:P0}";
        });
        UpdateApplyResult result = await _update.ApplyAsync(r, progress, _prepareExit, _exit, _cts.Token);
        if (!result.Relaunching)
        {
            IsDownloading = false;
            IsBusy = false;
            CanApply = true;               // let them try again
            Headline = "Update failed";
            Detail = result.Error ?? "The update could not be installed. Your current build is untouched.";
        }
        // On success the app is exiting — leave the "Downloading…" state on screen.
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (!string.IsNullOrWhiteSpace(_releaseUrl)) ShellLaunch.OpenUrl(_releaseUrl);
    }

    // Called from the window's Closed handler so an in-flight check/download stops
    // when the user dismisses the dialog.
    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }
    }

    private void Render(UpdateCheckResult r)
    {
        _releaseUrl = r.HtmlUrl;
        HasReleaseLink = !string.IsNullOrWhiteSpace(r.HtmlUrl);
        ReleaseNotes = r.Notes?.Trim() ?? "";
        HasReleaseNotes = ReleaseNotes.Length > 0;
        CanApply = false;

        switch (r.State)
        {
            case UpdateAvailability.UpToDate:
                Headline = "You're up to date";
                Detail = $"MudPlay {r.CurrentVersion} is the latest version.";
                HasReleaseNotes = false;          // notes are only meaningful for a newer build
                break;

            case UpdateAvailability.UpdateAvailable:
                Headline = $"Update available — {r.LatestVersion}";
                if (UpdatePlatform.IsSelfContainedInstall())
                {
                    long mb = r.Asset is { } a ? a.Size / (1024 * 1024) : 0;
                    Detail = $"A new version is ready ({mb} MB). MudPlay will download it, verify it, "
                             + "replace this build, and restart.";
                    CanApply = true;
                }
                else
                {
                    Detail = "A new version is available, but this build can't update itself — "
                             + "download it from the release page.";
                }
                break;

            case UpdateAvailability.NoAssetForPlatform:
                Headline = $"Update available — {r.LatestVersion}";
                Detail = "A new version is available, but there's no download for your platform. "
                         + "Grab it from the release page.";
                break;

            default: // Error
                Headline = "Couldn't check for updates";
                Detail = r.Error ?? "The update check failed. Check your connection and try again.";
                HasReleaseNotes = false;
                break;
        }
    }
}
