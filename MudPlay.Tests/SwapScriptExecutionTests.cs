using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using MudPlay.Services.Update;
using Xunit;

namespace MudPlay.Tests;

// Actually RUNS the posix swap helper against a throwaway sandbox laid out like a
// real update: an install folder holding MudPlay's files AND the user's own files
// (the folder may be Downloads), a staged new build, the list UpdatePlan writes
// from it, and the helper started the way UpdateInstaller starts it
// (SwapStartInfo, every value in the environment). String assertions can't catch
// what bites here: whether a user's file survives, whether the relaunch argv
// survives shell quoting, whether a failure puts every file back. The script only
// ever executes on a user's machine during a real update.
//
// Linux/macOS only: that helper is bash. The Windows helper can't run here, so its
// structure is pinned instead: per-file and list-driven, never a mirror, purge or
// folder delete, and every del guarded against naming a folder.
public sealed partial class SwapScriptExecutionTests
{
    // ----- POSIX, executed ------------------------------------------------------

    // The swap puts the new build's files in and deletes nothing: not the user's
    // files, and not a file an older MudPlay left (that is RetiredFiles' job, in code).
    [Fact]
    public void Posix_ReplacesTheBuildsFiles_DeletesNothing_LeavesTheUsersFilesByteForByte()
    {
        if (!Bash) return;
        using var box = new Sandbox(["MudPlay", "legacy.dat", "kept.dat"]);
        box.NewBuild(["MudPlay", "kept.dat", "extra.dat"]);
        Dictionary<string, byte[]> users = box.SnapshotUserFiles();
        string[] before = box.InstalledNames();

        box.Run(box.Plan(), profile: "Some BBS/Bob the Bard", reconnect: true);

        Assert.Equal(users, box.SnapshotUserFiles());
        Assert.Empty(before.Except(box.InstalledNames()));
        Assert.Equal("old legacy.dat", box.InstalledText("legacy.dat"));
        Assert.Contains("launched-new", box.InstalledText("MudPlay"));
        Assert.Equal("new kept.dat", box.InstalledText("kept.dat"));
        Assert.Equal("new extra.dat", box.InstalledText("extra.dat"));
        Assert.Equal(new[] { "--profile", "Some BBS/Bob the Bard", "--reconnect" }, box.Relaunched("new"));
        // The staged download and the backup inside it are gone, and nothing was put
        // beside the install folder.
        Assert.False(Directory.Exists(box.Stage), "the staging folder (and its backup) wasn't cleaned up");
        Assert.Equal(new[] { "install" }, box.EntriesBesideInstall());
        Assert.False(File.Exists(box.Installed("MudPlay-update-failed.log")));
        Assert.Empty(Directory.GetFiles(box.Install, "*.mudplay-update-*"));
    }

    [Fact]
    public void Posix_RelaunchesWithProfileOnlyWhenNotReconnecting()
    {
        if (!Bash) return;
        using var box = new Sandbox(["MudPlay"]);
        box.NewBuild(["MudPlay"]);
        box.Run(box.Plan(), profile: "Board/Char", reconnect: false);
        Assert.Equal(new[] { "--profile", "Board/Char" }, box.Relaunched("new"));
    }

    [Fact]
    public void Posix_RelaunchesBareWhenNothingToRestore()
    {
        if (!Bash) return;
        using var box = new Sandbox(["MudPlay"]);
        box.NewBuild(["MudPlay"]);
        box.Run(box.Plan(), profile: "", reconnect: false);
        Assert.Empty(box.Relaunched("new"));
    }

    // A browser saved the program as "MudPlay (1)": the new build goes in under that
    // name, and no second "MudPlay" appears beside it.
    [Fact]
    public void Posix_ARenamedProgram_IsUpdatedUnderItsOwnName()
    {
        if (!Bash) return;
        using var box = new Sandbox(["MudPlay (1)"], exeName: "MudPlay (1)");
        box.NewBuild(["MudPlay", "extra.dat"]);
        Dictionary<string, byte[]> users = box.SnapshotUserFiles();

        UpdatePlan plan = box.Plan();
        Assert.Equal("MudPlay", plan.NewExe);
        Assert.Equal(new[] { "extra.dat" }, plan.Install);
        box.Run(plan, profile: "Board/Char", reconnect: false);

        Assert.Equal(users, box.SnapshotUserFiles());
        Assert.Contains("launched-new", box.InstalledText("MudPlay (1)"));
        Assert.False(File.Exists(box.Installed("MudPlay")), "the new build went in under its archive name");
        Assert.Equal(new[] { "--profile", "Board/Char" }, box.Relaunched("new"));
    }

    // A swap that fails part-way puts back every file it replaced, takes out every
    // file it added, leaves the user's files alone, relaunches the old build, and
    // leaves the step log beside the executable for a bug report.
    [Fact]
    public void Posix_FailedSwap_RestoresTheOldFiles_AndLeavesTheUsersAlone()
    {
        if (!Bash) return;
        using var box = new Sandbox(["MudPlay", "legacy.dat", "kept.dat"]);
        // Sorted last, and taken out of the build after the plan is made, so every
        // other file is already in place when copying this one fails.
        box.NewBuild(["MudPlay", "kept.dat", "extra.dat", "zzz-broken.dat"]);
        Dictionary<string, byte[]> users = box.SnapshotUserFiles();
        Dictionary<string, byte[]> mudplay = box.SnapshotInstalled(["MudPlay", "legacy.dat", "kept.dat"]);
        UpdatePlan plan = box.Plan();
        Assert.Equal("zzz-broken.dat", plan.Install[^1]);
        File.Delete(Path.Combine(box.NewRoot, "zzz-broken.dat"));

        box.Run(plan, profile: "Board/Char", reconnect: true);

        Assert.Equal(users, box.SnapshotUserFiles());
        Assert.Equal(mudplay, box.SnapshotInstalled(["MudPlay", "legacy.dat", "kept.dat"]));
        Assert.False(File.Exists(box.Installed("extra.dat")), "a file the failed update added is still there");
        Assert.False(File.Exists(box.Installed("zzz-broken.dat")));
        Assert.Equal(new[] { "--profile", "Board/Char", "--reconnect" }, box.Relaunched("old"));
        string failLog = box.Installed("MudPlay-update-failed.log");
        Assert.True(File.Exists(failLog), "no failure log beside the executable");
        string log = File.ReadAllText(failLog);
        Assert.Contains("FAILED: putting 'zzz-broken.dat' in place failed", log);
        Assert.Contains("took out 'extra.dat'", log);
        Assert.DoesNotContain("took out 'zzz-broken.dat'", log);
        Assert.False(Directory.Exists(box.Stage), "a fully restored install kept its backup");
        // A failed helper keeps itself and its step log (in temp, in a real update).
        Assert.Equal(new[] { "install", "swap.log", "swap.sh" }, box.EntriesBesideInstall());
        Assert.Empty(Directory.GetFiles(box.Install, "*.mudplay-update-*"));
    }

    // A link to one of the user's folders appears under a file's name after the plan
    // was made: the swap refuses it and rolls back, and the rollback takes out
    // neither the link nor anything in the folder it points to.
    [Fact]
    public void Posix_ALinkToAFolderInTheWay_IsNeverTakenOut()
    {
        if (!Bash) return;
        using var box = new Sandbox(["MudPlay", "kept.dat"]);
        box.NewBuild(["MudPlay", "kept.dat", "zz-link.dat"]);
        Dictionary<string, byte[]> users = box.SnapshotUserFiles();
        Dictionary<string, byte[]> mudplay = box.SnapshotInstalled(["MudPlay", "kept.dat"]);
        UpdatePlan plan = box.Plan();
        Directory.CreateSymbolicLink(box.Installed("zz-link.dat"), box.Installed("notes"));

        box.Run(plan, profile: "", reconnect: false);

        Assert.True(new FileInfo(box.Installed("zz-link.dat")).LinkTarget is not null, "the user's link was taken out");
        Assert.Equal(users, box.SnapshotUserFiles());
        Assert.Equal(mudplay, box.SnapshotInstalled(["MudPlay", "kept.dat"]));
        Assert.Contains("'zz-link.dat' is a folder in the install", File.ReadAllText(box.Installed("MudPlay-update-failed.log")));
        box.Relaunched("old");
    }

    // A failure log name that is a link is never written through.
    [Fact]
    public void Posix_TheFailureLogIsNeverWrittenThroughALink()
    {
        if (!Bash) return;
        using var box = new Sandbox(["MudPlay"]);
        box.NewBuild(["MudPlay", "zzz-broken.dat"]);
        UpdatePlan plan = box.Plan();
        File.Delete(Path.Combine(box.NewRoot, "zzz-broken.dat"));
        File.CreateSymbolicLink(box.Installed("MudPlay-update-failed.log"), box.Installed("photo.jpg"));
        Dictionary<string, byte[]> users = box.SnapshotUserFiles();

        box.Run(plan, profile: "", reconnect: false);

        box.Relaunched("old");
        Assert.Equal(users, box.SnapshotUserFiles());
        Assert.True(new FileInfo(box.Installed("MudPlay-update-failed.log")).LinkTarget is not null);
    }

    // Started with a value missing, or a staging folder that isn't an update's own,
    // the helper does nothing at all: no file touched, nothing relaunched, no log.
    [Theory]
    [InlineData("dst")]
    [InlineData("exe")]
    [InlineData("stage")]
    [InlineData("install")]
    [InlineData("newexe")]
    [InlineData("foreignstage")]
    public void Posix_RefusesToStart_WhenAValueIsMissingOrTheStageIsntAnUpdates(string fault)
    {
        if (!Bash) return;
        using var box = new Sandbox(["MudPlay"]);
        box.NewBuild(["MudPlay"]);
        UpdatePlan plan = box.Plan();
        string list = plan.WriteList(box.Stage);
        string foreign = box.Foreign();
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "mine");
        SwapRequest request = box.Request(plan, list, profile: "", reconnect: false);
        request = fault switch
        {
            "dst" => request with { InstallDir = "" },
            "exe" => request with { Exe = "" },
            "stage" => request with { Stage = "" },
            "install" => request with { InstallList = "" },
            "newexe" => request with { NewExe = "" },
            _ => request with { Stage = foreign },
        };
        string[] before = box.InstalledNames();
        Dictionary<string, byte[]> users = box.SnapshotUserFiles();

        Assert.Equal(2, box.RunRaw(request));

        Assert.Equal(before, box.InstalledNames());
        Assert.Equal(users, box.SnapshotUserFiles());
        Assert.Contains("launched-old", box.InstalledText("MudPlay"));
        Assert.True(Directory.Exists(box.Stage));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(foreign, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(box.Root, "swap.log")));
        Assert.False(File.Exists(Path.Combine(box.Root, "launched-old")));
    }

    // ----- How the helper is started ------------------------------------------

    // Every value travels in the environment; cmd's command line names only the
    // script's own file name, so a folder named "R&D" can't end the command.
    [Fact]
    public void SwapStartInfo_PutsEveryValueInTheEnvironment_AndNothingTheUserNamedOnTheCommandLine()
    {
        var request = new SwapRequest("4242", @"C:\T\mudplay-update-1\staging", @"C:\T\mudplay-update-1\staging\MudPlay.exe",
            @"C:\Users\bob\Downloads\R&D 100%", @"C:\Users\bob\Downloads\R&D 100%\MudPlay (1).exe",
            @"C:\T\mudplay-update-1", "Board/Bob the Bard", "1", @"C:\T\mudplay-update-1\install.list");
        string script = Path.Combine(Path.GetTempPath(), "mudplay-swap-0123abcd.cmd");

        ProcessStartInfo win = UpdateInstaller.SwapStartInfo(script, windows: true, request);
        Assert.Equal("cmd.exe", win.FileName);
        Assert.Equal("/d /v:off /s /c \"\"mudplay-swap-0123abcd.cmd\"\"", win.Arguments);
        Assert.Empty(win.ArgumentList);
        Assert.Equal(Path.GetDirectoryName(script), win.WorkingDirectory);
        AssertEnvironment(win, request);

        ProcessStartInfo posix = UpdateInstaller.SwapStartInfo(script, windows: false, request);
        Assert.Equal(new[] { "bash", script }, posix.ArgumentList);
        AssertEnvironment(posix, request);
    }

    private static void AssertEnvironment(ProcessStartInfo psi, SwapRequest r)
    {
        Assert.Equal(r.Pid, psi.Environment["MUDPLAY_SWAP_PID"]);
        Assert.Equal(r.NewRoot, psi.Environment["MUDPLAY_SWAP_NEW"]);
        Assert.Equal(r.NewExe, psi.Environment["MUDPLAY_SWAP_NEWEXE"]);
        Assert.Equal(r.InstallDir, psi.Environment["MUDPLAY_SWAP_DST"]);
        Assert.Equal(r.Exe, psi.Environment["MUDPLAY_SWAP_EXE"]);
        Assert.Equal(r.Stage, psi.Environment["MUDPLAY_SWAP_STAGE"]);
        Assert.Equal(r.Profile, psi.Environment["MUDPLAY_SWAP_PROFILE"]);
        Assert.Equal(r.Reconnect, psi.Environment["MUDPLAY_SWAP_RECONNECT"]);
        Assert.Equal(r.InstallList, psi.Environment["MUDPLAY_SWAP_INSTALL"]);
    }

    // ----- Structure of both helpers -------------------------------------------

    [Fact]
    public void Posix_NeverMovesOrDeletesTheInstallFolder()
    {
        string s = SwapScriptBuilder.BuildPosix();
        Assert.Contains("DST=\"${MUDPLAY_SWAP_DST:-}\"", s);
        Assert.Contains("case \"$STAGE\" in *mudplay-update-*) ;; *) exit 2 ;; esac", s);
        Assert.DoesNotContain("$1\"; NEW", s);
        Assert.DoesNotContain("mv \"$DST\"", s);
        Assert.DoesNotContain("mv \"$NEW\" \"$DST\"", s);
        Assert.DoesNotContain(".bak", s);
        // The only recursive delete is the staging folder in temp.
        foreach (string line in s.Split('\n').Where(l => l.Contains("rm -rf")))
            Assert.Contains("rm -rf \"$STAGE\"", line);
        // Every other rm is the helper's own temp copy, log or self, or (in rollback
        // only) a file this update had just added, never a folder or a link to one.
        foreach (string line in s.Split('\n').Where(l => l.Contains("rm -f ")))
            Assert.Matches(@"rm -f (-- )?""\$(DST/\$(f|2)(\$SUF)?|LOG|0)""", line);
        string[] removesListed = s.Split('\n').Where(l => l.Contains("rm -f -- \"$DST/$f\"")).ToArray();
        Assert.Single(removesListed);
        Assert.Contains("(new in this update)", removesListed[0]);
        // The failure log never goes through a link.
        Assert.Contains("if [ -L \"$FAILLOG\" ] || [ -d \"$FAILLOG\" ]; then", s);
    }

    [Fact]
    public void Windows_IsPerFileAndListDriven_NeverAMirrorPurgeOrFolderDelete()
    {
        string s = SwapScriptBuilder.BuildWindows();
        Assert.Contains("set \"DST=%MUDPLAY_SWAP_DST%\"", s);
        Assert.Contains("set \"INSTALL=%MUDPLAY_SWAP_INSTALL%\"", s);
        // No value arrives as a positional argument (%~1 is a subroutine's own name).
        Assert.DoesNotMatch(@"%~[2-9]", s);
        Assert.Contains("for /f \"usebackq delims=\" %%F in (\"%INSTALL%\") do call :backup \"%%F\"", s);
        Assert.Contains("for /f \"usebackq delims=\" %%F in (\"%INSTALL%\") do call :install \"%%F\"", s);
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
        // Every del (but the script deleting itself) checks first, on the same line,
        // that its name isn't a folder, which del would empty.
        foreach (string line in lines.Where(l => l.Contains("del ", StringComparison.OrdinalIgnoreCase)
                                                 && !l.TrimStart().StartsWith("rem ", StringComparison.OrdinalIgnoreCase)))
        {
            if (line.Contains("del \"%~f0\"", StringComparison.Ordinal)) continue;
            Match target = DelTarget().Match(line);
            Assert.True(target.Success, $"unexpected delete: {line.Trim()}");
            string guard = "if not exist " + target.Groups[1].Value.Insert(target.Groups[1].Value.Length - 1, "\\");
            Assert.True(line.Contains(guard, StringComparison.Ordinal), $"unguarded delete: {line.Trim()}");
        }
        // The new executable's temporary copy is refused when something is in its way.
        Assert.Contains("if exist \"%EXE%%SUF%\" (", s);
        Assert.DoesNotContain("%EXE%.new", s);
        // A record of backups that can't be read takes nothing out (findstr 2).
        Assert.Contains("if errorlevel 2 (", Section(s, ":restore"));
        Assert.Contains("(>>\"%SAVED%\" echo %~1) || set \"WHY=", Section(s, ":backup"));
        Assert.Contains("(>>\"%TOUCHED%\" echo %~1) || set \"WHY=", Section(s, ":install"));
    }

    [Fact]
    public void Windows_RefusesToStart_WhenAValueIsMissingOrTheStageIsntAnUpdates()
    {
        string s = SwapScriptBuilder.BuildWindows();
        foreach (string v in new[] { "PID", "NEW", "NEWEXE", "DST", "EXE", "STAGE", "INSTALL" })
            Assert.Contains($"if not defined {v} exit /b 2", s);
        Assert.Contains("if \"%STAGE:mudplay-update-=%\"==\"%STAGE%\" exit /b 2", s);
        // Nothing is logged, waited for or touched before the checks.
        int lastCheck = s.LastIndexOf("exit /b 2", StringComparison.Ordinal);
        Assert.True(lastCheck < s.IndexOf(">\"%LOG%\"", StringComparison.Ordinal));
        Assert.True(lastCheck < s.IndexOf(":waitloop", StringComparison.Ordinal));
    }

    [Fact]
    public void Windows_SwapsTheExecutableByRename_AfterTheOtherFiles()
    {
        string s = SwapScriptBuilder.BuildWindows();
        Assert.Contains("for %%I in (\"%EXE%\") do set \"EXENAME=%%~nxI\"", s);
        Assert.Contains("if /i \"%~1\"==\"%EXENAME%\" goto :eof", Section(s, ":install"));
        int others = s.IndexOf("do call :install", StringComparison.Ordinal);
        int copy = s.IndexOf("copy /y \"%NEWEXE%\" \"%EXE%%SUF%\"", StringComparison.Ordinal);
        int aside = s.IndexOf("move /y \"%EXE%\" \"%OLD%\"", StringComparison.Ordinal);
        int into = s.IndexOf("move /y \"%EXE%%SUF%\" \"%EXE%\"", StringComparison.Ordinal);
        Assert.True(others >= 0 && others < copy && copy < aside && aside < into);
        // A failed move back is said in the failure log, with the copy's name.
        int back = s.IndexOf("move /y \"%OLD%\" \"%EXE%\"", StringComparison.Ordinal);
        Assert.True(back > into);
        Assert.Contains("FAILED to put the old %EXENAME% back: it is kept as \"%OLD%\"", s);
        // Rollback removes the temporary copy only when this run made it, and only
        // while the executable itself is there.
        Assert.Contains("if defined MADENEW if exist \"%EXE%\" if not exist \"%EXE%%SUF%\\\" del /f /q \"%EXE%%SUF%\"", s);
        Assert.Contains("goto rollback", s);
    }

    [Fact]
    public void Windows_CarriesTheExecutablesPermissionsAcross()
    {
        string s = SwapScriptBuilder.BuildWindows();
        int save = s.IndexOf("icacls \"%EXE%\" /save \"%ACL%\"", StringComparison.Ordinal);
        int renameAside = s.IndexOf("move /y \"%EXE%\" \"%OLD%\"", StringComparison.Ordinal);
        int renameIn = s.IndexOf("move /y \"%EXE%%SUF%\" \"%EXE%\"", StringComparison.Ordinal);
        int restore = s.IndexOf("icacls \"%ACLDIR%\" /restore \"%ACL%\"", StringComparison.Ordinal);
        int start = s.IndexOf("start \"\" \"%EXE%\"", StringComparison.Ordinal);
        Assert.True(save >= 0 && save < renameAside);
        Assert.True(restore > renameIn && restore < start);
        // A drive-root install ("D:\") is named "D:\." so icacls doesn't read "\"" as a quote.
        Assert.Contains("if \"%ACLDIR:~-1%\"==\"\\\" set \"ACLDIR=%ACLDIR%.\"", s);
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
        // The failure log isn't written through a link (attribute l) or over a folder.
        Assert.Contains("if defined FAILATTR if not \"%FAILATTR:l=%\"==\"%FAILATTR%\" set \"FAILOK=\"", s);
        Assert.Contains("if defined FAILOK copy /y \"%LOG%\" \"%FAILLOG%\"", s);
        Assert.Contains("(goto) 2>nul & del \"%~f0\"", s);
        // A restore that fails keeps the backup in temp; one that succeeds drops it.
        Assert.Contains("if not defined RESTOREFAILED rmdir /s /q \"%STAGE%\"", s);
    }

    [GeneratedRegex(@"del /f /q (""[^""]+"")")]
    private static partial Regex DelTarget();

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
    // root/mudplay-update-test (the update's work folder, as in temp) holding new/
    // (the extracted build), root/swap.sh, and the relaunch markers.
    private sealed class Sandbox : IDisposable
    {
        private static readonly string[] UserFiles = ["photo.jpg", "other.exe", "notes/todo.txt", "notes/deep/plan.md", "MudPlay-old.exe"];

        private readonly string _exeName;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mudplay-swaptest-" + Guid.NewGuid().ToString("N"));
        public string Install => Path.Combine(Root, "install");
        public string Stage => Path.Combine(Root, "mudplay-update-test");
        public string NewRoot => Path.Combine(Stage, "new");

        public Sandbox(string[] oldFiles, string exeName = "MudPlay")
        {
            _exeName = exeName;
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
            foreach (string f in oldFiles)
                WriteMudPlayFile(Install, f, "old");
        }

        public void NewBuild(string[] files)
        {
            Directory.CreateDirectory(NewRoot);
            foreach (string f in files)
                WriteMudPlayFile(NewRoot, f, "new");
        }

        public UpdatePlan Plan()
        {
            UpdatePlan? plan = UpdatePlan.Build(Install, _exeName, NewRoot, out string? refusal);
            Assert.True(plan is not null, refusal);
            return plan!;
        }

        // A folder of the user's that is not an update's staging folder.
        public string Foreign()
        {
            string dir = Path.Combine(Root, "not-a-stage");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // A PID that has already exited, so the helper's wait falls straight through.
        public SwapRequest Request(UpdatePlan plan, string installList, string profile, bool reconnect)
        {
            using Process probe = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"exit 0\"") { UseShellExecute = false })!;
            probe.WaitForExit();
            return new SwapRequest(probe.Id.ToString(), NewRoot, Path.Combine(NewRoot, plan.NewExe), Install,
                Installed(_exeName), Stage, profile, reconnect ? "1" : "", installList);
        }

        // Run the helper as UpdateInstaller does: the list written into the work
        // folder, the helper started through SwapStartInfo.
        public void Run(UpdatePlan plan, string profile, bool reconnect)
            => RunRaw(Request(plan, plan.WriteList(Stage), profile, reconnect));

        public int RunRaw(SwapRequest request)
        {
            string script = Path.Combine(Root, "swap.sh");
            File.WriteAllText(script, SwapScriptBuilder.BuildPosix());
            using Process p = Process.Start(UpdateInstaller.SwapStartInfo(script, windows: false, request))!;
            Assert.True(p.WaitForExit(60_000), "the swap helper didn't finish");
            return p.ExitCode;
        }

        // The argv the relaunched executable saw; which build ran ("old" or "new").
        public string[] Relaunched(string build)
        {
            string done = Path.Combine(Root, $"launched-{build}");
            for (int i = 0; i < 200 && !File.Exists(done); i++) Thread.Sleep(50);
            Assert.True(File.Exists(done), $"the {build} build was never relaunched");
            return File.ReadAllLines(Path.Combine(Root, $"argv-{build}.txt"));
        }

        public string Installed(string name) => Path.Combine(Install, name);
        public string InstalledText(string name) => File.ReadAllText(Installed(name));

        // Every file and folder under the install, as paths relative to it.
        public string[] InstalledNames() =>
            Directory.GetFileSystemEntries(Install, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(Install, p)).Order(StringComparer.Ordinal).ToArray();

        public Dictionary<string, byte[]> SnapshotUserFiles() =>
            UserFiles.ToDictionary(u => u, u => File.ReadAllBytes(Path.Combine(Install, u)));

        public Dictionary<string, byte[]> SnapshotInstalled(string[] names) =>
            names.ToDictionary(n => n, n => File.ReadAllBytes(Installed(n)));

        public string[] EntriesBesideInstall() =>
            Directory.GetFileSystemEntries(Root)
                .Select(Path.GetFileName).OfType<string>()
                .Where(n => !n.StartsWith("argv-", StringComparison.Ordinal) && !n.StartsWith("launched-", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToArray();

        // The executable is a stub that records its argv and which build it is; any
        // other file just says which build it came from.
        private void WriteMudPlayFile(string folder, string name, string build)
        {
            string path = Path.Combine(folder, name);
            if (name != _exeName && name != "MudPlay")
            {
                File.WriteAllText(path, $"{build} {name}");
                return;
            }
            string argv = Path.Combine(Root, $"argv-{build}.txt");
            string done = Path.Combine(Root, $"launched-{build}");
            File.WriteAllText(path,
                "#!/usr/bin/env bash\n" +
                $"for a in \"$@\"; do printf '%s\\n' \"$a\"; done > '{argv}'\n" +
                $"touch '{done}'\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
