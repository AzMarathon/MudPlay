using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MudPlay.Services.Update;

// The dangerous half: downloads the release archive, verifies its SHA-256 against
// the release's SHA256SUMS.txt (we're about to RUN this, so an unverified download
// is refused), extracts it, then spawns a detached swap helper and asks the app to
// exit. Nothing in the live install is touched until the app has exited and the
// helper runs — so any failure here leaves the current install intact.
public sealed class UpdateInstaller
{
    private const string ChecksumAssetName = "SHA256SUMS.txt";

    private readonly HttpClient _http;
    private readonly LogService? _log;

    public UpdateInstaller(HttpClient http, LogService? log)
    {
        _http = http;
        _log = log;
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        UpdateCheckResult result, IProgress<double>? progress, Action onExit, CancellationToken ct)
    {
        if (result.Asset is not { } asset) return UpdateApplyResult.Fail("no asset to download");
        if (UpdatePlatform.InstallDirectory() is not { } installDir)
            return UpdateApplyResult.Fail("couldn't locate the install directory");
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return UpdateApplyResult.Fail("couldn't locate the running executable");

        string work = Path.Combine(Path.GetTempPath(), "mudplay-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            string archive = Path.Combine(work, asset.Name);

            _log?.Info("Update", $"downloading {asset.Name} ({asset.Size / (1024 * 1024)} MB)");
            await DownloadAsync(asset, archive, progress, ct).ConfigureAwait(false);

            // Verify against a FRESH SHA256SUMS (never a cached hash) — the gate on
            // running downloaded code.
            string? expected = await FetchExpectedHashAsync(asset.DownloadUrl, asset.Name, ct).ConfigureAwait(false);
            if (expected is null)
                return UpdateApplyResult.Fail("no published checksum for this build — refusing to install unverified");
            string actual = ComputeSha256(archive);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                _log?.Warn("Update", $"checksum mismatch: expected {expected}, got {actual}");
                return UpdateApplyResult.Fail("download failed verification (checksum mismatch) — not installed");
            }
            _log?.Info("Update", "checksum verified — extracting");

            string staging = Path.Combine(work, "staging");
            Directory.CreateDirectory(staging);
            await ExtractAsync(archive, staging, asset.Name, ct).ConfigureAwait(false);

            string exeName = Path.GetFileName(exe);
            string newRoot = ResolveNewRoot(staging, exeName);

            // Write the swap helper OUTSIDE the work dir (it deletes work at the end,
            // so `rm -rf work` mustn't remove the helper before it's done). The helper
            // deletes itself as its final step, so no update leftovers survive in temp.
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string scriptPath = Path.Combine(Path.GetTempPath(),
                $"mudplay-swap-{Guid.NewGuid():N}{(windows ? ".cmd" : ".sh")}");
            File.WriteAllText(scriptPath, windows ? SwapScriptBuilder.BuildWindows() : SwapScriptBuilder.BuildPosix());
            if (!windows) TryChmodExec(scriptPath);

            SpawnSwap(scriptPath, windows,
                Environment.ProcessId.ToString(), newRoot, installDir, exe, work);
            _log?.Info("Update", "swap helper launched — exiting to let it replace the install");

            onExit();
            return UpdateApplyResult.Started();
        }
        catch (OperationCanceledException)
        {
            TryDelete(work);
            return UpdateApplyResult.Fail("update cancelled");
        }
        catch (Exception ex)
        {
            _log?.Warn("Update", $"apply failed: {ex.GetType().Name}: {ex.Message}");
            TryDelete(work);
            return UpdateApplyResult.Fail(ex.Message);
        }
    }

    private async Task DownloadAsync(UpdateAsset asset, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        using HttpResponseMessage resp = await _http
            .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? asset.Size;
        await using Stream net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await net.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0) progress?.Report(Math.Clamp((double)read / total, 0, 1));
        }
    }

    private async Task<string?> FetchExpectedHashAsync(string assetUrl, string assetName, CancellationToken ct)
    {
        // The asset and the checksums file share a release directory:
        // …/releases/download/<tag>/<name>. Swap the filename to reach SHA256SUMS.
        int slash = assetUrl.LastIndexOf('/');
        if (slash < 0) return null;
        string sumsUrl = assetUrl[..(slash + 1)] + ChecksumAssetName;
        try
        {
            string text = await _http.GetStringAsync(sumsUrl, ct).ConfigureAwait(false);
            return ReleaseParser.ParseSha256Sums(text).TryGetValue(assetName, out string? h) ? h : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task ExtractAsync(string archive, string destDir, string assetName, CancellationToken ct)
    {
        if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(archive, destDir, overwriteFiles: true), ct)
                .ConfigureAwait(false);
            return;
        }
        // .tar.gz — gunzip then untar. TarFile sets unix permissions from the entries.
        await Task.Run(() =>
        {
            using FileStream fs = File.OpenRead(archive);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            System.Formats.Tar.TarFile.ExtractToDirectory(gz, destDir, overwriteFiles: true);
        }, ct).ConfigureAwait(false);
    }

    // The publisher may tar the publish folder's CONTENTS (flat) or the folder
    // itself (one subdir). Descend into a lone subdirectory when the exe isn't at the
    // top; otherwise the staging dir is the new app root.
    private static string ResolveNewRoot(string staging, string exeName)
    {
        if (File.Exists(Path.Combine(staging, exeName))) return staging;
        string[] dirs = Directory.GetDirectories(staging);
        string[] files = Directory.GetFiles(staging);
        if (dirs.Length == 1 && files.Length == 0) return dirs[0];
        return staging;
    }

    private static void SpawnSwap(string scriptPath, bool windows, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        if (windows)
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(scriptPath);
        }
        else
        {
            psi.FileName = "/usr/bin/env";
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add(scriptPath);
        }
        foreach (string a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    private static string ComputeSha256(string path)
    {
        using FileStream fs = File.OpenRead(path);
        byte[] hash = SHA256.HashData(fs);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryChmodExec(string path)
    {
        if (OperatingSystem.IsWindows()) return;   // no unix mode + satisfies CA1416
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch { /* unsupported filesystem — the env-bash spawn doesn't need the +x */ }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
