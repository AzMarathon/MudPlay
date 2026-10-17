using System.IO;
using System.Runtime.CompilerServices;
using MudPlay.Services;

namespace MudPlay.Tests;

// Clears the test-* folders a crashed or killed run left under the game-data root
// and the BBS folder. AppPaths' roots are fixed at static init and can't be pointed
// elsewhere per test, so test classes work in tagged test-* folders there and remove
// them in Dispose; a run that dies first leaves them behind, and an install's data
// folder would fill with them.
//
// Only a folder untouched for StaleAfter is taken. Two test processes on one data
// root (overlapping runs) each run this sweep, and taking every test-* folder
// emptied the other process's sets in the middle of its tests. A live run's folders
// are seconds old: a whole suite takes well under a minute.
internal static class TestSessionCleanup
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    [ModuleInitializer]
    public static void Sweep()
    {
        DateTime cutoff = DateTime.UtcNow - StaleAfter;
        SweepFolder(AppPaths.GameDataRoot, cutoff);
        try
        {
            string bbsParent = Path.GetDirectoryName(AppPaths.BbsFolder("dummy"))!;
            SweepFolder(bbsParent, cutoff);
        }
        catch { /* best-effort */ }
    }

    private static void SweepFolder(string root, DateTime cutoff)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (string dir in Directory.EnumerateDirectories(root, "test-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff) Directory.Delete(dir, recursive: true);
                }
                catch { /* best-effort — leaked folders that won't delete now will get cleaned next run */ }
            }
        }
        catch { /* best-effort */ }
    }
}
