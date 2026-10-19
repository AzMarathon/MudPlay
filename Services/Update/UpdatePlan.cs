using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MudPlay.Services.Update;

// Exactly which files an update puts into the install folder, worked out before the
// app exits so the swap helper only ever works through one list: the files of the
// verified release archive's app root, each replaced or added beside the
// executable. The helper removes nothing; files an older release left behind are
// removed by the new build itself at startup (RetiredFiles).
//
// The folder holding the executable may be Downloads or the Desktop, full of the
// user's own files; an earlier updater that mirrored a whole new build over it
// deleted every one of them. So every name in the archive must be a plain file name
// that lands directly beside the executable. A release is a single-file build, so
// anything else (a subfolder, a link, a name with path or shell characters in it)
// means the download isn't what a release looks like, and nothing is installed.
public sealed class UpdatePlan
{
    public IReadOnlyList<string> Install { get; }
    // The Install files already in the folder, for the log.
    public IReadOnlyList<string> Replaced { get; }

    private UpdatePlan(IReadOnlyList<string> install, IReadOnlyList<string> replaced)
    {
        Install = install;
        Replaced = replaced;
    }

    // Why name can't be a file the update installs, or null when it can. The
    // character set is held to what a published file is ever called, so a name
    // can't carry anything a cmd or bash line would read as syntax.
    public static string? WhyNotEntry(string name)
    {
        if (name.Length == 0) return "is empty";
        if (name[0] is '/' or '\\' || (name.Length > 1 && name[1] == ':')) return "is an absolute path";
        if (name.Split('/', '\\').Any(seg => seg == "..")) return "climbs out of the folder with '..'";
        if (name.Contains('/') || name.Contains('\\')) return "names a file in a subfolder";
        if (name.Length > 255) return "is too long for a file name";
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+'))
            return "has characters no MudPlay file is named with";
        if (!PathGuard.IsPlainName(name)) return "isn't a plain file name";
        return null;
    }

    // The plan for swapping the extracted build in newRoot into installDir, or null
    // with the reason the update is refused (nothing in the install has been touched).
    public static UpdatePlan? Build(string installDir, string exeName, string newRoot, out string? refusal)
    {
        refusal = null;
        if (Directory.GetDirectories(newRoot).Length > 0)
        {
            refusal = "the download holds folders as well as files — not installed";
            return null;
        }
        var install = new List<string>();
        foreach (string path in Directory.GetFiles(newRoot).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            string? why = WhyNotEntry(name)
                          ?? (PathGuard.Child(installDir, name) is null ? "would land outside the install folder" : null)
                          ?? (new FileInfo(path).LinkTarget is not null ? "is a link, not a file" : null)
                          ?? (Directory.Exists(Path.Combine(installDir, name)) ? "is the name of a folder in the install" : null);
            if (why is not null)
            {
                refusal = $"the download's file {PathGuard.Show(name)} {why} — not installed";
                return null;
            }
            install.Add(name);
        }

        StringComparer names = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        if (!install.Contains(exeName, names))
        {
            refusal = $"the download has no {exeName} — not installed";
            return null;
        }
        List<string> replaced = install.Where(f => File.Exists(Path.Combine(installDir, f))).ToList();
        return new UpdatePlan(install, replaced);
    }

    // Write the list the swap helper reads, one name per line, into workDir.
    public string WriteList(string workDir)
    {
        string list = Path.Combine(workDir, "install.list");
        File.WriteAllLines(list, Install);
        return list;
    }

    // One line for the program log.
    public string Describe()
    {
        string[] added = Install.Except(Replaced, StringComparer.Ordinal).ToArray();
        return $"replacing {List(Replaced)}; adding {List(added)}; removing nothing";
    }

    private static string List(IReadOnlyCollection<string> files) =>
        files.Count == 0 ? "nothing" : string.Join(", ", files);
}
