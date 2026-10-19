using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MudPlay.Services.Update;

// Exactly which files an update touches in the install folder, worked out before
// the app exits so the swap helpers only ever act on two lists:
//   Install — every file the new build's manifest names: replaced, or added.
//   Remove  — files the OLD manifest (the one beside the running executable) names
//             that the new one dropped, and that are still there as files.
// Nothing else in the folder is touched. The folder holding the executable may be
// Downloads or the Desktop, full of the user's own files; an earlier updater that
// mirrored a whole new build over it deleted every one of them.
//
// An install from before manifests existed has no old manifest. It gets the new
// build's files and loses nothing: without a record of what it was given, no file
// in that folder can be known to be MudPlay's.
public sealed class UpdatePlan
{
    public IReadOnlyList<string> Install { get; }
    public IReadOnlyList<string> Remove { get; }
    public bool HadOldManifest { get; }
    // The Install files that are already in the folder, for the log.
    public IReadOnlyList<string> Replaced { get; }

    private UpdatePlan(IReadOnlyList<string> install, IReadOnlyList<string> remove,
        bool hadOldManifest, IReadOnlyList<string> replaced)
    {
        Install = install;
        Remove = remove;
        HadOldManifest = hadOldManifest;
        Replaced = replaced;
    }

    // Checked before anything is downloaded: a damaged manifest beside the running
    // executable stops the update, since its list is what removals are taken from.
    public static string? WhyNotFromHere(string installDir)
    {
        ManifestRead old = InstallManifest.Read(installDir);
        return old.Error is { } error ? $"the install's own file list is damaged ({error}) — nothing was changed" : null;
    }

    // The plan for swapping the extracted build in newRoot into installDir, or null
    // with the reason the update is refused (nothing in the install has been touched).
    // A new build that carries no manifest gets one written into newRoot from its own
    // files, so the install keeps a record of what it was given for the next update.
    public static UpdatePlan? Build(string installDir, string exeName, string newRoot, out string? refusal)
    {
        refusal = WhyNotFromHere(installDir);
        if (refusal is not null) return null;
        ManifestRead old = InstallManifest.Read(installDir);

        if (!InstallManifest.Read(newRoot).Present)
        {
            if (Directory.GetDirectories(newRoot).Length > 0)
            {
                refusal = "the downloaded build has folders and no file list — not installed";
                return null;
            }
            InstallManifest.Write(newRoot, Directory.GetFiles(newRoot).Select(Path.GetFileName).OfType<string>());
        }
        ManifestRead fresh = InstallManifest.Read(newRoot);
        if (fresh.Entries is not { } install)
        {
            refusal = $"the downloaded build's file list is damaged ({fresh.Error}) — not installed";
            return null;
        }

        StringComparer names = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        if (!install.Contains(exeName, names))
        {
            refusal = $"the downloaded build has no {exeName} — not installed";
            return null;
        }
        foreach (string f in install)
        {
            // Parse has already held every entry to a plain name directly inside its folder.
            string target = Path.Combine(installDir, f);
            if (!File.Exists(Path.Combine(newRoot, f)))
            {
                refusal = $"the downloaded build is missing {f}, which its file list names — not installed";
                return null;
            }
            if (Directory.Exists(target))
            {
                refusal = $"there's a folder named {f} where MudPlay's file goes — nothing was changed";
                return null;
            }
        }

        // Compared ignoring case everywhere: a file kept by mistake is harmless, one
        // removed by mistake is not.
        var keep = new HashSet<string>(install, StringComparer.OrdinalIgnoreCase);
        List<string> remove = (old.Entries ?? [])
            .Where(f => !keep.Contains(f) && File.Exists(Path.Combine(installDir, f)))
            .ToList();
        List<string> replaced = install.Where(f => File.Exists(Path.Combine(installDir, f))).ToList();
        return new UpdatePlan(install, remove, old.Present, replaced);
    }

    // Write the two lists the swap helper reads, one name per line, into workDir.
    public (string InstallList, string RemoveList) WriteLists(string workDir)
    {
        string installList = Path.Combine(workDir, "install.list");
        string removeList = Path.Combine(workDir, "remove.list");
        File.WriteAllLines(installList, Install);
        File.WriteAllLines(removeList, Remove);
        return (installList, removeList);
    }

    // One line for the program log: what is replaced, added and removed, and why
    // nothing is removed when there was no old manifest.
    public string Describe()
    {
        string[] added = Install.Except(Replaced, StringComparer.Ordinal).ToArray();
        string removal = Remove.Count > 0
            ? $"removing {string.Join(", ", Remove)} (dropped from the release, per the old file list)"
            : HadOldManifest
                ? "removing nothing"
                : "removing nothing (no file list beside the old build, so no file there is known to be MudPlay's)";
        return $"replacing {List(Replaced)}; adding {List(added)}; {removal}";
    }

    // The install folder's manifest state, for the bug report.
    public static string DescribeInstall(string? installDir)
    {
        if (installDir is null) return "(unknown)";
        ManifestRead m = InstallManifest.Read(installDir);
        string state = !m.Present ? "no file list (installed before 3.185.9; an update adds files and removes none)"
            : m.Entries is { } e ? $"file list of {e.Count}: {string.Join(", ", e)}"
            : $"file list DAMAGED, updates refused ({m.Error})";
        return $"{installDir} — {state}";
    }

    private static string List(IReadOnlyCollection<string> files) =>
        files.Count == 0 ? "nothing" : string.Join(", ", files);
}
