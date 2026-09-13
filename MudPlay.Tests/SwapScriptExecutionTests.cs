using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MudPlay.Services.Update;
using Xunit;

namespace MudPlay.Tests;

// Actually RUNS the posix swap helper against throwaway directories. String
// assertions can't catch the thing that bites here — whether the relaunch argv
// survives shell quoting — and this script only ever executes on a user's machine
// during a real update, where a mistake means their client comes back with no
// profile (or doesn't come back at all). So the argv is pinned by execution.
//
// Linux/macOS only: the helper is bash. The windows variant stays string-tested.
public sealed class SwapScriptExecutionTests
{
    [Fact]
    public void Posix_RelaunchesWithProfileAndReconnect()
    {
        string[]? argv = RunSwap("Some BBS/Bob the Bard", reconnect: true);
        if (argv is null) return;   // not a bash platform
        Assert.Equal(new[] { "--profile", "Some BBS/Bob the Bard", "--reconnect" }, argv);
    }

    [Fact]
    public void Posix_RelaunchesBareWhenNothingToRestore()
    {
        string[]? argv = RunSwap("", reconnect: false);
        if (argv is null) return;
        Assert.Empty(argv);
    }

    [Fact]
    public void Posix_RelaunchesWithProfileOnlyWhenNotReconnecting()
    {
        string[]? argv = RunSwap("Board/Char", reconnect: false);
        if (argv is null) return;
        Assert.Equal(new[] { "--profile", "Board/Char" }, argv);
    }

    // Build the install/staging layout the helper expects, run it, and return the
    // argv the relaunched "executable" saw. Null when the platform isn't bash.
    private static string[]? RunSwap(string profileToken, bool reconnect)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return null;

        string root = Path.Combine(Path.GetTempPath(), "mudplay-swaptest-" + Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "install");
        string stage = Path.Combine(root, "stage");
        string newRoot = Path.Combine(stage, "new");
        string argvFile = Path.Combine(root, "argv.txt");
        string doneFile = Path.Combine(root, "done");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(newRoot);

        // The "new build": a stub that records how it was invoked, one arg per line,
        // then touches a marker so the poll below can tell "no args" apart from "not
        // launched yet". It lands at the install path once the helper swaps the trees
        // over, which is exactly the binary the relaunch line fires.
        string stub = Path.Combine(newRoot, "MudPlay");
        File.WriteAllText(stub,
            "#!/usr/bin/env bash\n" +
            $"for a in \"$@\"; do printf '%s\\n' \"$a\"; done > '{argvFile}'\n" +
            $"touch '{doneFile}'\n");
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(install, "MudPlay"), "#!/usr/bin/env bash\nexit 0\n");

        string script = Path.Combine(root, "swap.sh");
        File.WriteAllText(script, SwapScriptBuilder.BuildPosix());

        // A PID that has already exited, so the helper's wait loop falls straight
        // through instead of holding the test for its full 60 s budget.
        using (Process probe = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"exit 0\"") { UseShellExecute = false })!)
        {
            probe.WaitForExit();
            RunBash(script, probe.Id.ToString(), newRoot, install,
                    Path.Combine(install, "MudPlay"), stage, profileToken, reconnect ? "1" : "");
        }

        // The relaunch is backgrounded by the helper, so poll rather than assume it
        // has already run by the time the helper returns.
        for (int i = 0; i < 100 && !File.Exists(doneFile); i++) Thread.Sleep(50);
        bool relaunched = File.Exists(doneFile);
        string[] argv = relaunched ? File.ReadAllLines(argvFile) : Array.Empty<string>();

        try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        Assert.True(relaunched, "swap helper never relaunched the swapped-in executable");
        return argv;
    }

    private static void RunBash(string script, params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/env") { UseShellExecute = false };
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add(script);
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
