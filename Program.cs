using Avalonia;
using MudPlay.Services;

namespace MudPlay;

// Entry point. Boots the Avalonia application and hands off to App.
internal static class Program
{
    // Avalonia requires a single-threaded apartment on Windows for COM
    // interop (clipboard, drag/drop, native dialogs). Marking Main with
    // [STAThread] is what guarantees that.
    [STAThread]
    public static void Main(string[] args)
    {
        // Cap glibc malloc arenas early to hold down the long-run native RSS floor
        // (Linux/glibc only; no-op elsewhere). Done first so it bounds arena growth
        // before the renderer starts churning text-shaping allocations.
        NativeHeapTuning.CapMallocArenas();

        // --profile <name>[,<name>…]: this instance loads the first named profile;
        // each extra name launches its own instance (one process per profile) so a
        // single command can bring up several sessions. Each child is spawned with a
        // SINGLE --profile, so it parses to one token and never re-spawns.
        var profileTokens = StartupOptions.ParseProfileTokens(args);
        if (profileTokens.Count > 0)
        {
            StartupOptions.RequestedProfileToken = profileTokens[0];
            int spawned = 0;
            for (int i = 1; i < profileTokens.Count; i++)
                if (TryLaunchProfileInstance(profileTokens[i])) spawned++;
            StartupOptions.SpawnedSiblings = spawned;
        }

        // Install the crash net before anything can fault. CrashReporter.Guard
        // captures exceptions escaping the UI run loop; Install hooks the
        // out-of-band CLR failure channels. Either way a fatal error lands a
        // Crash-<timestamp>.md on the Desktop instead of vanishing.
        CrashReporter.Install();
        CrashReporter.Guard(() =>
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args));
    }

    // Spawn another copy of this executable bound to a single profile. Best-effort:
    // a launch failure never blocks this instance's own startup (the user just sees
    // one fewer window). Returns true when the child process started.
    private static bool TryLaunchProfileInstance(string profileToken)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--profile");
            psi.ArgumentList.Add(profileToken);
            return System.Diagnostics.Process.Start(psi) is not null;
        }
        catch
        {
            // Sibling launch is a convenience; swallow so the primary instance
            // still comes up. (No LogService yet — AppServices isn't built here.)
            return false;
        }
    }

    // Builds the Avalonia configuration. The XAML previewer in IDEs also
    // calls this method by reflection, so its signature must stay stable.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()       // Pick the right backend (X11, Win32, ...).
#if DEBUG
            .WithDeveloperTools()      // F12 inspector in debug builds.
#endif
            .WithInterFont()           // Fall-back UI font for chrome/text boxes.
            .LogToTrace();             // Route Avalonia diagnostics to the trace listener.
}
