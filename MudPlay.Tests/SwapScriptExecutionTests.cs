using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MudPlay.Services.Update;
using Xunit;

namespace MudPlay.Tests;

// Actually RUNS the posix swap helper against a throwaway sandbox laid out like a
// real update: an install folder holding MudPlay's files from an old manifest AND
// the user's own files (the folder may be Downloads), a staged new build with its
// manifest, and the two lists UpdatePlan writes from them. String assertions can't
// catch what bites here: whether a user's file survives, whether the relaunch argv
// survives shell quoting, whether a failure puts every file back. The script only
// ever executes on a user's machine during a real update.
//
// Linux/macOS only: that helper is bash. The Windows helper can't run here, so its
// structure is pinned instead: per-file and list-driven, never a mirror, purge or
// folder delete.
public sealed class SwapScriptExecutionTests
{
    // ----- POSIX, executed ------------------------------------------------------

    [Fact]
    public void Posix_ReplacesOnlyListedFiles_AndLeavesTheUsersFilesByteForByte()
    {
        if (!Bash) return;
        using var box = new Sandbox(oldManifest: ["MudPlay", "MudPlay.files", "legacy.dat", "kept.dat"]);
        box.NewBuild(["MudPlay", "MudPlay.files", "kept.dat", "extra.dat"]);
        Dictionary<string, byte[]> users = box.SnapshotUserFiles();

        box.Run(box.Plan(), profile: "Some BBS/Bob the Bard", reconnect: true);

        Assert.Equal(users, box.SnapshotUserFiles());
        Assert.False(File.Exists(box.Installed("legacy.dat")), "a file the new manifest dropped is still there");
        Assert.Contains("launched-new", box.InstalledText("MudPlay"));
        Assert.Equal("new kept.dat", box.InstalledText("kept.dat"));
        Assert.Equal("new extra.dat", box.InstalledText("extra.dat"));
        Assert.Contains("extra.dat", box.InstalledText("MudPlay.files"));
        Assert.Equal(new[] { "--profile", "Some BBS/Bob the Bard", "--reconnect" }, box.Relaunched("new"));
        // The staged download and the backup inside it are gone, and nothing was put
        // beside the install folder.
        Assert.False(Directory.Exists(box.Stage), "the staging folder (and its backup) wasn't cleaned up");
        Assert.Equal(new[] { "install" }, box.EntriesBesideInstall());
        Assert.False(File.Exists(box.Installed("MudPlay-update-failed.log")));
    }

    // An install from before manifests: the new build's files go in, nothing goes out.
    [Fact]
    public void Posix_PreManifestInstall_AddsAndReplaces_RemovesNothing()
    {
        if (!Bash) return;
        using var box = new Sandbox(oldManifest: null, oldFiles: ["MudPlay", "legacy.dat"]);
        box.NewBuild(["MudPlay", "MudPlay.files"]);
        Dictionary<string, byte[]> users = box.SnapshotUserFiles();

        UpdatePlan plan = box.Plan();
        Assert.Empty(plan.Remove);
        box.Run(plan, profile: "", reconnect: false);

        Assert.Equal(users, box.SnapshotUserFiles());
        Assert.Equal("old legacy.dat", box.InstalledText("legacy.dat"));
        Assert.Contains("launched-new", box.InstalledText("MudPlay"));
        Assert.True(File.Exists(box.Installed("MudPlay.files")));
        Assert.Empty(box.Relaunched("new"));
    }

    [Fact]
    public void Posix_RelaunchesWithProfileOnlyWhenNotReconnecting()
    {
        if (!Bash) return;
        using var box = new Sandbox(oldManifest: ["MudPlay", "MudPlay.files"]);
        box.NewBuild(["MudPlay", "MudPlay.files"]);
        box.Run(box.Plan(), profile: "Board/Char", reconnect: false);
        Assert.Equal(new[] { "--profile", "Board/Char" }, box.Relaunched("new"));
    }

    // A swap that fails part-way puts back every file it replaced, takes out every
    // file it added, leaves the user's files alone, relaunches the old build, and
    // leaves the step log beside the executable for a bug report.
    [Fact]
    public void Posix_FailedSwap_RestoresTheOldFiles_AndLeavesTheUsersAlone()
    {
        if (!Bash) return;
        using var box = new Sandbox(oldManifest: ["MudPlay", "MudPlay.files", "legacy.dat", "kept.dat"]);
        // Listed last, and taken out of the build after the plan is made, so every
        // other file is already in place when copying this one fails.
        box.NewBuild(["MudPlay", "MudPlay.files", "kept.dat", "extra.dat", "zzz-broken.dat"]);
        Dictionary<string, byte[]> users = box.SnapshotUserFiles();
        Dictionary<string, byte[]> mudplay = box.SnapshotInstalled(["MudPlay", "MudPlay.files", "legacy.dat", "kept.dat"]);
        UpdatePlan plan = box.Plan();
        File.Delete(Path.Combine(box.NewRoot, "zzz-broken.dat"));

        box.Run(plan, profile: "Board/Char", reconnect: true);

        Assert.Equal(users, box.SnapshotUserFiles());
        Assert.Equal(mudplay, box.SnapshotInstalled(["MudPlay", "MudPlay.files", "legacy.dat", "kept.dat"]));
        Assert.False(File.Exists(box.Installed("extra.dat")), "a file the failed update added is still there");
        Assert.False(File.Exists(box.Installed("zzz-broken.dat")));
        Assert.Equal(new[] { "--profile", "Board/Char", "--reconnect" }, box.Relaunched("old"));
        string failLog = box.Installed("MudPlay-update-failed.log");
        Assert.True(File.Exists(failLog), "no failure log beside the executable");
        Assert.Contains("FAILED: putting 'zzz-broken.dat' in place failed", File.ReadAllText(failLog));
        Assert.False(Directory.Exists(box.Stage), "a fully restored install kept its backup");
        // A failed helper keeps itself and its step log (in temp, in a real update).
        Assert.Equal(new[] { "install", "swap.log", "swap.sh" }, box.EntriesBesideInstall());
        Assert.Empty(Directory.GetFiles(box.Install, "*.mudplay-update-*"));
    }

    // ----- Structure of both helpers -------------------------------------------

    [Fact]
    public void Posix_NeverMovesOrDeletesTheInstallFolder()
    {
        string s = SwapScriptBuilder.BuildPosix();
        Assert.Contains("INSTALL=\"$8\"; REMOVE=\"$9\"", s);
        Assert.Contains("BAK=\"$STAGE/backup\"", s);
        Assert.DoesNotContain("mv \"$DST\"", s);
        Assert.DoesNotContain("mv \"$NEW\" \"$DST\"", s);
        Assert.DoesNotContain(".bak", s);
        // The only recursive delete is the staging folder in temp.
        foreach (string line in s.Split('\n').Where(l => l.Contains("rm -rf")))
            Assert.Contains("rm -rf \"$STAGE\"", line);
        // Every other rm names one file, never a folder.
        foreach (string line in s.Split('\n').Where(l => l.Contains("rm -f ")))
            Assert.Matches(@"rm -f (-- )?""\$(DST/\$(f|2)(\$SUF)?|LOG|0)""", line);
    }

    [Fact]
    public void Windows_IsPerFileAndListDriven_NeverAMirrorPurgeOrFolderDelete()
    {
        string s = SwapScriptBuilder.BuildWindows();
        Assert.Contains("set \"INSTALL=%~8\"", s);
        Assert.Contains("set \"REMOVE=%~9\"", s);
        Assert.Contains("for /f \"usebackq delims=\" %%F in (\"%INSTALL%\") do call :install \"%%F\"", s);
        Assert.Contains("for /f \"usebackq delims=\" %%F in (\"%REMOVE%\") do call :remove \"%%F\"", s);
        // The backup lives in the staging folder in temp, never beside the install.
        Assert.Contains("set \"BAK=%STAGE%\\backup\"", s);
        Assert.DoesNotContain(".bak", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("robocopy", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/MIR", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/PURGE", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("move /y \"%DST%\" ", s, StringComparison.OrdinalIgnoreCase);
        string[] lines = s.Split('\n');
        foreach (string line in lines.Where(l => l.Contains("rmdir", StringComparison.OrdinalIgnoreCase)))
            Assert.Contains("rmdir /s /q \"%STAGE%\"", line);
        // del only ever names one of the helper's own files or one listed name.
        string[] mayDelete =
        [
            "\"%DST%\\%~1\"", "\"%DST%\\%~1%SUF%\"", "\"%OLD%\"", "\"%ACL%\"", "\"%LOG%\"", "\"%EXE%.new\"", "\"%~f0\"",
        ];
        foreach (string line in lines.Where(l => l.Contains("del ", StringComparison.OrdinalIgnoreCase)
                                                 && !l.TrimStart().StartsWith("rem ", StringComparison.OrdinalIgnoreCase)))
            Assert.True(mayDelete.Any(t => line.Contains("del /f /q " + t) || line.Contains("del " + t)),
                $"unexpected delete: {line.Trim()}");
        // Given a folder, del empties it: each listed name is checked for being one first.
        Assert.True(Section(s, ":remove").IndexOf("if exist \"%DST%\\%~1\\\" goto :eof", StringComparison.Ordinal)
                    < Section(s, ":remove").IndexOf("del /f /q", StringComparison.Ordinal));
        Assert.Contains("if not exist \"%DST%\\%~1\\\" del /f /q \"%DST%\\%~1\"", Section(s, ":restore"));
        Assert.Contains("if exist \"%DST%\\%~1\\\" goto :eof", Section(s, ":backup"));
    }

    [Fact]
    public void Windows_SwapsTheExecutableByRename_AfterTheOtherFiles()
    {
        string s = SwapScriptBuilder.BuildWindows();
        Assert.Contains("set \"EXENAME=%~nx4\"", s);
        Assert.Contains("if /i \"%~1\"==\"%EXENAME%\" goto :eof", Section(s, ":install"));
        int others = s.IndexOf("do call :install", StringComparison.Ordinal);
        int copy = s.IndexOf("copy /y \"%NEW%\\%EXENAME%\" \"%EXE%.new\"", StringComparison.Ordinal);
        int aside = s.IndexOf("move /y \"%EXE%\" \"%OLD%\"", StringComparison.Ordinal);
        int into = s.IndexOf("move /y \"%EXE%.new\" \"%EXE%\"", StringComparison.Ordinal);
        Assert.True(others >= 0 && others < copy && copy < aside && aside < into);
        Assert.Contains("move /y \"%OLD%\" \"%EXE%\"", s);
        Assert.Contains(":rollback", s);
        Assert.Contains("goto rollback", s);
    }

    [Fact]
    public void Windows_CarriesTheExecutablesPermissionsAcross()
    {
        string s = SwapScriptBuilder.BuildWindows();
        int save = s.IndexOf("icacls \"%EXE%\" /save \"%ACL%\"", StringComparison.Ordinal);
        int renameAside = s.IndexOf("move /y \"%EXE%\" \"%OLD%\"", StringComparison.Ordinal);
        int renameIn = s.IndexOf("move /y \"%EXE%.new\" \"%EXE%\"", StringComparison.Ordinal);
        int restore = s.IndexOf("icacls \"%DST%\" /restore \"%ACL%\"", StringComparison.Ordinal);
        int start = s.IndexOf("start \"\" \"%EXE%\"", StringComparison.Ordinal);
        Assert.True(save >= 0 && save < renameAside);
        Assert.True(restore > renameIn && restore < start);
        // The saved permissions never outlive the swap, failed or not.
        Assert.Equal(2, s.Split("del /f /q \"%ACL%\"").Length - 1);
    }

    [Fact]
    public void Windows_WaitsRelaunchesLogsAndDeletesItself()
    {
        string s = SwapScriptBuilder.BuildWindows();
        Assert.Contains("tasklist /FI \"PID eq %PID%\"", s);
        Assert.Contains("set ARGS=--profile \"%PROFILE%\"", s);
        Assert.Contains("set ARGS=%ARGS% --reconnect", s);
        Assert.Contains("start \"\" \"%EXE%\" %ARGS%", s);
        Assert.Contains("set \"FAILLOG=%DST%\\MudPlay-update-failed.log\"", s);
        Assert.Contains("copy /y \"%LOG%\" \"%FAILLOG%\"", s);
        Assert.Contains("(goto) 2>nul & del \"%~f0\"", s);
        // A restore that fails keeps the backup in temp; one that succeeds drops it.
        Assert.Contains("if not defined RESTOREFAILED rmdir /s /q \"%STAGE%\"", s);
    }

    // The text of a cmd subroutine, from its label to the next one.
    private static string Section(string script, string label)
    {
        script = script.ReplaceLineEndings("\n");
        int from = script.IndexOf("\n" + label + "\n", StringComparison.Ordinal);
        Assert.True(from >= 0, $"no {label} subroutine");
        int to = script.IndexOf("\n:", from + label.Length + 2, StringComparison.Ordinal);
        return to < 0 ? script[from..] : script[from..to];
    }

    private static bool Bash => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    // root/install (the folder MudPlay runs from, with user files beside it),
    // root/stage (the update's work folder, as in temp) holding stage/new (the
    // extracted build), root/swap.sh, and the relaunch markers.
    private sealed class Sandbox : IDisposable
    {
        private static readonly string[] UserFiles = ["photo.jpg", "other.exe", "notes/todo.txt", "notes/deep/plan.md", "MudPlay-old.exe"];

        private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-swaptest-" + Guid.NewGuid().ToString("N"));
        public string Install => Path.Combine(_root, "install");
        public string Stage => Path.Combine(_root, "stage");
        public string NewRoot => Path.Combine(Stage, "new");

        public Sandbox(string[]? oldManifest, string[]? oldFiles = null)
        {
            Directory.CreateDirectory(Install);
            var rng = new Random(1234);
            foreach (string u in UserFiles)
            {
                string path = Path.Combine(Install, u);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] bytes = new byte[4096];
                rng.NextBytes(bytes);
                File.WriteAllBytes(path, bytes);
            }
            foreach (string f in oldFiles ?? oldManifest!.Where(f => f != InstallManifest.FileName))
                WriteMudPlayFile(Install, f, "old");
            if (oldManifest is not null)
                InstallManifest.Write(Install, oldManifest.Where(f => f != InstallManifest.FileName));
        }

        public void NewBuild(string[] manifest)
        {
            Directory.CreateDirectory(NewRoot);
            foreach (string f in manifest.Where(f => f != InstallManifest.FileName))
                WriteMudPlayFile(NewRoot, f, "new");
            InstallManifest.Write(NewRoot, manifest.Where(f => f != InstallManifest.FileName));
        }

        public UpdatePlan Plan()
        {
            UpdatePlan? plan = UpdatePlan.Build(Install, "MudPlay", NewRoot, out string? refusal);
            Assert.True(plan is not null, refusal);
            return plan!;
        }

        // Run the helper as UpdateInstaller does: the lists written into the work
        // folder, and a PID that has already exited so the wait falls straight through.
        public void Run(UpdatePlan plan, string profile, bool reconnect)
        {
            (string installList, string removeList) = plan.WriteLists(Stage);
            string script = Path.Combine(_root, "swap.sh");
            File.WriteAllText(script, SwapScriptBuilder.BuildPosix());
            using Process probe = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"exit 0\"") { UseShellExecute = false })!;
            probe.WaitForExit();
            var psi = new ProcessStartInfo("/usr/bin/env") { UseShellExecute = false };
            foreach (string a in new[]
                     {
                         "bash", script, probe.Id.ToString(), NewRoot, Install, Path.Combine(Install, "MudPlay"), Stage,
                         profile, reconnect ? "1" : "", installList, removeList,
                     })
                psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            Assert.True(p.WaitForExit(60_000), "the swap helper didn't finish");
        }

        // The argv the relaunched executable saw; which build ran ("old" or "new").
        public string[] Relaunched(string build)
        {
            string done = Path.Combine(_root, $"launched-{build}");
            for (int i = 0; i < 200 && !File.Exists(done); i++) Thread.Sleep(50);
            Assert.True(File.Exists(done), $"the {build} build was never relaunched");
            return File.ReadAllLines(Path.Combine(_root, $"argv-{build}.txt"));
        }

        public string Installed(string name) => Path.Combine(Install, name);
        public string InstalledText(string name) => File.ReadAllText(Installed(name));

        public Dictionary<string, byte[]> SnapshotUserFiles() =>
            UserFiles.ToDictionary(u => u, u => File.ReadAllBytes(Path.Combine(Install, u)));

        public Dictionary<string, byte[]> SnapshotInstalled(string[] names) =>
            names.ToDictionary(n => n, n => File.ReadAllBytes(Installed(n)));

        public string[] EntriesBesideInstall() =>
            Directory.GetFileSystemEntries(_root)
                .Select(Path.GetFileName).OfType<string>()
                .Where(n => !n.StartsWith("argv-", StringComparison.Ordinal) && !n.StartsWith("launched-", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToArray();

        // The executable is a stub that records its argv and which build it is; any
        // other file just says which build it came from.
        private void WriteMudPlayFile(string folder, string name, string build)
        {
            string path = Path.Combine(folder, name);
            if (name != "MudPlay")
            {
                File.WriteAllText(path, $"{build} {name}");
                return;
            }
            string argv = Path.Combine(_root, $"argv-{build}.txt");
            string done = Path.Combine(_root, $"launched-{build}");
            File.WriteAllText(path,
                "#!/usr/bin/env bash\n" +
                $"for a in \"$@\"; do printf '%s\\n' \"$a\"; done > '{argv}'\n" +
                $"touch '{done}'\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
