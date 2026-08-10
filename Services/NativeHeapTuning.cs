using System.Runtime.InteropServices;

namespace MudPlay.Services;

// glibc malloc tuning for the Linux native-memory floor.
//
// On Linux/glibc the allocator spins up to 8×CPU per-thread arenas of ~64 MB each
// and retains freed blocks inside them rather than returning memory to the OS. A
// long-running, allocation-churny session (the terminal renderer shapes text every
// frame) therefore shows a high, stable native RSS that no GC touches — the
// resident set is glibc arena retention, not a managed leak. Capping the arena
// count trades a little multi-threaded malloc contention for a much lower native
// floor.
//
// glibc-only. Windows uses its own heap manager and macOS uses libmalloc — neither
// has these arenas nor a mallopt to honor, so the call is skipped there (zero
// effect, nothing to cap). Non-glibc Linux libcs (musl) may lack the symbol; the
// call is best-effort and any failure is swallowed.
//
// Setting MALLOC_ARENA_MAX in the launch environment is strictly stronger — it
// applies before the runtime creates its first arena, whereas this in-process cap
// runs once the CLR is already up. This is the code-only fallback; it still bounds
// all further arena growth over the session, which is where the long-run RSS ramp
// comes from.
public static class NativeHeapTuning
{
    // glibc <malloc.h>: mallopt(M_ARENA_MAX, n) caps the arena count. M_ARENA_MAX is
    // the option code -8; 2 is the value commonly recommended for threaded .NET.
    private const int M_ARENA_MAX = -8;

    [DllImport("libc", SetLastError = false)]
    private static extern int mallopt(int param, int value);

    // Cap glibc's malloc arenas. No-op off Linux/glibc; failures are swallowed —
    // this is an optional memory tuning, never a correctness dependency.
    public static void CapMallocArenas(int maxArenas = 2)
    {
        if (!OperatingSystem.IsLinux()) return;
        try { mallopt(M_ARENA_MAX, maxArenas); }
        catch { /* non-glibc libc (e.g. musl) has no mallopt — tuning just doesn't apply */ }
    }
}
