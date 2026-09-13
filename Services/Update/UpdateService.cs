using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MudPlay.Services.Update;

// Self-update against GitHub Releases. Checks whether a newer published build
// exists for THIS platform, caches the verdict, and (on explicit user request)
// downloads + verifies + swaps it in and relaunches. It never installs on its
// own — a startup check only sets the availability flag the splash + Help menu
// read; the actual replace is user-initiated.
//
// Threading: CheckAsync runs its HTTP off the UI thread and raises
// AvailabilityChanged from wherever the await resumes — subscribers (splash,
// menu) marshal to the UI thread themselves.
public sealed class UpdateService : IDisposable
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Tehshortbus/MudPlay/releases/latest";
    private const string RawContentBase =
        "https://raw.githubusercontent.com/Tehshortbus/MudPlay";
    private const string ChecksumAssetName = "SHA256SUMS.txt";

    private readonly HttpClient _http;
    private readonly LogService? _log;
    private bool _disposed;

    // The most recent check's verdict — null until the first check runs.
    public UpdateCheckResult? Last { get; private set; }

    public bool UpdateAvailable => Last?.UpdateAvailable == true;

    // Raised after every check that changes the availability verdict (may fire on a
    // background thread — marshal in the handler).
    public event Action? AvailabilityChanged;

    public UpdateService(LogService? log = null)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub's API rejects requests with no User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"MudPlay/{AppInfo.Version}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    // Query GitHub for the latest release and decide whether it's a newer build with
    // an asset for this platform. Pure network + parsing — no side effects beyond
    // caching the result and raising AvailabilityChanged. Safe to call anywhere
    // (including a dev build); only ApplyAsync cares whether we're a real install.
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        string current = AppInfo.Version;
        try
        {
            string json = await _http.GetStringAsync(LatestReleaseApi, ct).ConfigureAwait(false);
            if (ReleaseParser.ParseRelease(json) is not { } rel)
                return Store(UpdateCheckResult.Failed(current, "could not parse the latest release"));

            if (!ReleaseParser.IsNewer(current, rel.Version))
                return Store(new(UpdateAvailability.UpToDate, current, rel.Version,
                    null, null, rel.HtmlUrl, rel.Notes, null));

            // A newer build: show its CHANGELOG entry, not the hand-authored release
            // body (that's publish boilerplate — the asset table + checksum notes).
            // Fall back to the release body if the changelog can't be fetched/parsed.
            string? notes = await TryFetchChangelogNotesAsync(rel, ct).ConfigureAwait(false) ?? rel.Notes;

            UpdateAsset? asset = rel.Assets.FirstOrDefault(a => UpdatePlatform.MatchesCurrentPlatform(a.Name));
            if (asset is null)
                return Store(new(UpdateAvailability.NoAssetForPlatform, current, rel.Version,
                    null, null, rel.HtmlUrl, notes, null));

            string? sha = await TryResolveChecksumAsync(rel, asset.Name, ct).ConfigureAwait(false);
            _log?.Info("Update", $"update available: {current} → {rel.Version} ({asset.Name}, {asset.Size / (1024 * 1024)} MB)");
            return Store(new(UpdateAvailability.UpdateAvailable, current, rel.Version,
                asset, sha, rel.HtmlUrl, notes, null));
        }
        catch (OperationCanceledException)
        {
            return Store(UpdateCheckResult.Failed(current, "check cancelled"));
        }
        catch (Exception ex)
        {
            _log?.Debug("Update", $"update check failed: {ex.GetType().Name}: {ex.Message}");
            return Store(UpdateCheckResult.Failed(current, ex.Message));
        }
    }

    // Download + verify + swap-in + relaunch the update in `result`. Delegates the
    // risky part to UpdateInstaller; returns its outcome. `onExit` is invoked to
    // close the app once the detached swap helper is running (the helper waits for
    // this process to exit before replacing files). Refuses in a dev build.
    public async Task<UpdateApplyResult> ApplyAsync(
        UpdateCheckResult result, IProgress<double>? progress, Action onExit, CancellationToken ct = default)
    {
        if (!result.UpdateAvailable || result.Asset is null)
            return UpdateApplyResult.Fail("no update to apply");
        if (!UpdatePlatform.IsSelfContainedInstall())
            return UpdateApplyResult.Fail("self-update isn't available for this build — download it from the release page");

        var installer = new UpdateInstaller(_http, _log);
        return await installer.ApplyAsync(result, progress, onExit, ct).ConfigureAwait(false);
    }

    // Fetch CHANGELOG.md at the release's tag and pull out that version's entry — the
    // real "what's new" the update window shows. Best-effort: a null (repo has no
    // CHANGELOG at that ref, network hiccup, unparsable) falls back to the release body.
    private async Task<string?> TryFetchChangelogNotesAsync(UpdateRelease rel, CancellationToken ct)
    {
        string url = $"{RawContentBase}/{rel.RawTag}/CHANGELOG.md";
        try
        {
            string md = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            return ChangelogExtractor.TopEntry(md, rel.Version);
        }
        catch
        {
            return null;
        }
    }

    // Fetch SHA256SUMS.txt (if the release carries it) and pull out the checksum for
    // assetName. Best-effort at check time — ApplyAsync re-fetches + enforces it, so
    // a null here just means "we'll verify at download time".
    private async Task<string?> TryResolveChecksumAsync(UpdateRelease rel, string assetName, CancellationToken ct)
    {
        UpdateAsset? sums = rel.Assets.FirstOrDefault(
            a => a.Name.Equals(ChecksumAssetName, StringComparison.OrdinalIgnoreCase));
        if (sums is null) return null;
        try
        {
            string text = await _http.GetStringAsync(sums.DownloadUrl, ct).ConfigureAwait(false);
            return ReleaseParser.ParseSha256Sums(text).TryGetValue(assetName, out string? h) ? h : null;
        }
        catch
        {
            return null;
        }
    }

    private UpdateCheckResult Store(UpdateCheckResult r)
    {
        bool changed = Last?.State != r.State || Last?.LatestVersion != r.LatestVersion;
        Last = r;
        if (changed) AvailabilityChanged?.Invoke();
        return r;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
