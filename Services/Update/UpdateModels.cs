using System.Collections.Generic;

namespace MudPlay.Services.Update;

// One downloadable file attached to a GitHub release.
public sealed record UpdateAsset(string Name, string DownloadUrl, long Size);

// A parsed GitHub release: the raw tag ("v3.78.0"), its version ("3.78.0"), the
// release notes body, the web page URL, and every attached asset.
public sealed record UpdateRelease(
    string RawTag,
    string Version,
    string? Notes,
    string HtmlUrl,
    IReadOnlyList<UpdateAsset> Assets);

// How a check turned out.
public enum UpdateAvailability
{
    UpToDate,          // the latest release is not newer than what's running
    UpdateAvailable,   // a newer release exists AND has an asset for this platform
    NoAssetForPlatform,// a newer release exists but nothing matches this OS/arch
    Error,             // the check itself failed (network / parse / rate limit)
}

// The result of an update check — everything the UI + the apply flow need. Asset /
// Sha256 are populated only when State == UpdateAvailable.
public sealed record UpdateCheckResult(
    UpdateAvailability State,
    string CurrentVersion,
    string? LatestVersion,
    UpdateAsset? Asset,
    string? Sha256,
    string? HtmlUrl,
    string? Notes,
    string? Error)
{
    public bool UpdateAvailable => State == UpdateAvailability.UpdateAvailable;

    public static UpdateCheckResult Failed(string current, string error) =>
        new(UpdateAvailability.Error, current, null, null, null, null, null, error);
}

// Outcome of an apply attempt. Relaunching == true means the swap helper is running
// and the app is about to exit; the caller should quit. On failure nothing was
// changed (the install is untouched — the swap only happens after this exits).
public sealed record UpdateApplyResult(bool Relaunching, string? Error)
{
    public static UpdateApplyResult Started() => new(true, null);
    public static UpdateApplyResult Fail(string error) => new(false, error);
}
