using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace MudPlay.Services;

// What the process holds beyond the managed heap: the figures that name a native
// leak. A client up for days at 26 GB of private bytes over a 500 MB managed heap
// has a leak no GC figure shows; these say where to look. Native is the process's
// own memory less the GC's committed memory: what the GC does not account for
// (Skia's caches and bitmaps, the window system's buffers, native libraries,
// malloc arenas). On Windows and macOS the process's own memory is private bytes,
// memory committed to it. On Linux private bytes are VmData, address space
// reserved rather than used (every thread's stack reservation, mappings never
// touched), which overstates native by hundreds of MB; there it is RssAnon, the
// anonymous memory actually resident, with private bytes kept beside it.
//
// Read once a minute by the memory log and once per bug or hang report, off the
// UI thread except for the bug report. None of it touches a window.
public readonly record struct ProcessResources(
    long? PrivateBytes,
    long GcCommitted,
    long Fragmented,
    long Gen2Size,
    long LohSize,
    long LohFragmented,
    long PohSize,
    long PinnedObjects,
    long? GcHandles,
    int? Threads,
    int? Handles,
    int PoolThreads,
    long Timers,
    int? GdiObjects,
    int? UserObjects,
    long? SkiaCacheBytes,
    long? SkiaCacheLimit,
    long? FontCacheBytes,
    long? FontCacheLimit,
    int? FontCacheEntries,
    long? ResidentAnon = null)
{
    // Never below zero: resident memory can be less than the GC has committed
    // (pages committed, never touched). A figure the OS gives as 0 (macOS may) is
    // taken as not read.
    public long? NativeBytes => NativeBase is { } b ? Math.Max(0, b - GcCommitted) : null;

    private long? NativeBase => ResidentAnon is > 0 ? ResidentAnon : PrivateBytes is > 0 ? PrivateBytes : null;

    // What NativeBytes was measured from.
    public string NativeSource => ResidentAnon is > 0 ? "resident anonymous memory (RssAnon)" : "private bytes";

    // gcHandles: the GC handle count the runtime last reported, or null when no
    // listener is on to hear it (it has no API of its own; PerformanceMonitor
    // hears it while collecting). renderer: read Skia's caches too. Left out of a
    // hang report: the frozen UI thread may be inside Skia, holding the lock its
    // cache figures are read under.
    public static ProcessResources Read(long? gcHandles, bool renderer)
    {
        long? privateBytes = null;
        int? threads = null, handles = null;
        try
        {
            using Process self = Process.GetCurrentProcess();
            // On Windows one refresh reads private bytes, threads and handles
            // together; elsewhere each is a /proc read.
            privateBytes = self.PrivateMemorySize64;
            threads = self.Threads.Count;
            handles = self.HandleCount;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                   or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // No process figures on this platform: what was read stands, the rest are "?".
        }

        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        ReadOnlySpan<GCGenerationInfo> gens = gc.GenerationInfo;
        // [gen0, gen1, gen2, LOH, POH]; the length guards keep a leaner runtime
        // from throwing.
        (int? gdi, int? user) = OperatingSystem.IsWindows() ? GuiObjects() : (null, null);
        (long? skia, long? skiaLimit, long? font, long? fontLimit, int? fontEntries) = renderer ? Skia() : default;
        return new ProcessResources(
            privateBytes, gc.TotalCommittedBytes, gc.FragmentedBytes,
            gens.Length > 2 ? gens[2].SizeAfterBytes : 0,
            gens.Length > 3 ? gens[3].SizeAfterBytes : 0,
            gens.Length > 3 ? gens[3].FragmentationAfterBytes : 0,
            gens.Length > 4 ? gens[4].SizeAfterBytes : 0,
            gc.PinnedObjectsCount, gcHandles, threads, handles, ThreadPool.ThreadCount, Timer.ActiveCount,
            gdi, user, skia, skiaLimit, font, fontLimit, fontEntries,
            OperatingSystem.IsLinux() ? ReadRssAnon() : null);
    }

    // RssAnon from /proc/self/status, given there in kB.
    private static long? ReadRssAnon()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("RssAnon:", StringComparison.Ordinal)) continue;
                string[] parts = line["RssAnon:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb)
                    ? kb * 1024 : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No /proc to read (a sandbox): native falls back to private bytes.
        }
        return null;
    }

    private static (int?, int?) GuiObjects()
    {
        try
        {
            return ((int)GetGuiResources(CurrentProcess, GrGdiObjects), (int)GetGuiResources(CurrentProcess, GrUserObjects));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, null);   // a Windows without user32 (Server Core, Nano): left out
        }
    }

    // Skia's process-wide caches: decoded images and other CPU-side resources,
    // and the glyph strikes. The GPU context's cache is Avalonia's own and has no
    // public reader.
    private static (long?, long?, long?, long?, int?) Skia()
    {
        try
        {
            return (SKGraphics.GetResourceCacheTotalBytesUsed(), SKGraphics.GetResourceCacheTotalByteLimit(),
                SKGraphics.GetFontCacheUsed(), SKGraphics.GetFontCacheLimit(), SKGraphics.GetFontCacheCountUsed());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            return default;   // no native Skia loaded (a headless run): left out
        }
    }

    // The bug report's and the hang report's lines, so both say the same.
    // contents: ContentSizes.Describe. activity: the session's totals.
    public IReadOnlyList<(string Key, string Value)> ReportLines(string contents, ActivityCounters.Counts activity)
    {
        List<(string, string)> lines =
        [
            ("Native memory (outside the GC's committed memory)",
                $"{Size(NativeBytes)}, {NativeSource} less GC committed: "
                + (ResidentAnon is > 0
                    ? $"resident anonymous {Size(ResidentAnon)}, private {Size(PrivateBytes)} (virtual on Linux), "
                    : $"private {Size(PrivateBytes)}, ")
                + $"GC committed {Size(GcCommitted)}, "
                + $"fragmented {Size(Fragmented)}, gen2 {Size(Gen2Size)}, LOH {Size(LohSize)} ({Size(LohFragmented)} fragmented), POH {Size(PohSize)}"),
            ("Threads and handles",
                $"{Count(Threads)} threads ({PoolThreads} in the pool), {Count(Handles)} OS handles, "
                + $"{(GcHandles is { } g ? g.ToString(CultureInfo.InvariantCulture) : "? (heard only while Auto-collect logs is on)")} GC handles, "
                + $"{PinnedObjects} pinned objects, {Timers} timers"),
        ];
        if (GdiObjects is not null || UserObjects is not null)
            lines.Add(("GDI / USER objects", $"{Count(GdiObjects)} / {Count(UserObjects)} (Windows stops drawing past 10,000 of either)"));
        if (SkiaCacheBytes is not null)
            lines.Add(("Skia caches", $"resources {Size(SkiaCacheBytes)} of {Size(SkiaCacheLimit)}; "
                + $"fonts {Size(FontCacheBytes)} of {Size(FontCacheLimit)}, {Count(FontCacheEntries)} entries"));
        lines.Add(("Window contents", contents));
        lines.Add(("Activity this session", $"{Many(activity.Lines, "line")} received, "
            + $"{Many(activity.Renders, "terminal render")}, {Many(activity.MapRedraws, "map redraw")}"));
        return lines;
    }

    private static string Many(long n, string what) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + what + (n == 1 ? string.Empty : "s");

    // The memory log's columns after the GC's own: key=value, "?" for a figure
    // not read, and the Linux, Windows and Skia figures only where they exist.
    public string Columns()
    {
        string line = $"native={Mb(NativeBytes)} "
            + (ResidentAnon is > 0 ? $"rssanon={Mb(ResidentAnon)} " : string.Empty)
            + $"threads={Count(Threads)} handles={Count(Handles)} "
            + $"gchandles={Count(GcHandles)} pinned={PinnedObjects} pool={PoolThreads} timers={Timers}";
        if (GdiObjects is not null || UserObjects is not null) line += $" gdi={Count(GdiObjects)} user={Count(UserObjects)}";
        if (SkiaCacheBytes is not null)
            line += $" skia={Mb(SkiaCacheBytes)}/{Mb(SkiaCacheLimit)} fonts={Mb(FontCacheBytes)}/{Mb(FontCacheLimit)}:{Count(FontCacheEntries)}";
        return line;
    }

    public static string Mb(long? bytes) => bytes is { } b
        ? (b / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + "MB"
        : "?";

    private static string Size(long? bytes) => bytes is { } b ? HangReport.SizeOf(b) : "?";

    private static string Count(long? n) => n is { } v ? v.ToString(CultureInfo.InvariantCulture) : "?";

    // GetCurrentProcess's pseudo-handle: needs no opening or closing.
    private static readonly IntPtr CurrentProcess = new(-1);
    private const uint GrGdiObjects = 0, GrUserObjects = 1;

    // GDI and USER object counts have no managed API, and they are the Windows
    // native leak a managed figure never shows: each is capped per process, and
    // past the cap windows stop drawing. Classic DllImport, like the rest of the
    // interop here; never called off Windows (Read guards on IsWindows).
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
}
