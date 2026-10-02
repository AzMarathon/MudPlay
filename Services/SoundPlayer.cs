using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MudPlay.Game.Sounds;

namespace MudPlay.Services;

// Plays a sound through whatever the operating system already provides: pw-play /
// paplay / aplay on Linux, afplay on macOS, the multimedia API on Windows. No audio
// library is bundled.
//
// Play returns at once. Resolving the sound (writing a built-in tone to disk the
// first time), starting the player and waiting for it to end all happen on a pool
// thread, so a cue never costs the caller — the UI thread, the wire pump — more
// than a counter bump. At most MaxConcurrent sounds run together; one past that is
// dropped rather than queued, since a late sound is worse than a missing one.
public sealed class SoundPlayer
{
    private const int MaxConcurrent = 4;
    private const int MaxFailuresLogged = 10;
    private static readonly TimeSpan PlayerTimeout = TimeSpan.FromSeconds(30);

    private readonly string _tonesDir;
    private readonly LogService? _log;
    private readonly object _toneLock = new();
    private readonly HashSet<string> _tonesWritten = new(StringComparer.Ordinal);
    private int _active;
    private readonly HashSet<string> _failuresLogged = new(StringComparer.Ordinal);

    // Linux: the player found on PATH, looked up once.
    private readonly Lazy<(string Exe, LinuxPlayer Kind)?> _linuxPlayer;
    private enum LinuxPlayer { PipeWire, Pulse, Alsa }

    public SoundPlayer(string tonesDir, LogService? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(tonesDir);
        _tonesDir = tonesDir;
        _log = log;
        _linuxPlayer = new Lazy<(string, LinuxPlayer)?>(FindLinuxPlayer);
    }

    // sound is a built-in tone ("tone:chime") or a file path; volume is 0–100.
    public void Play(string sound, int volume)
    {
        if (string.IsNullOrWhiteSpace(sound) || volume <= 0) return;
        if (Interlocked.Increment(ref _active) > MaxConcurrent)
        {
            Interlocked.Decrement(ref _active);
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                if (ResolvePath(sound) is { } path) Output(path, Math.Clamp(volume, 0, 100) / 100.0);
            }
            catch (Exception ex)
            {
                // Each distinct failure once, and only the first few: a machine with no
                // player would otherwise write a line per cue.
                bool first;
                lock (_failuresLogged)
                    first = _failuresLogged.Count < MaxFailuresLogged && _failuresLogged.Add(ex.Message);
                if (first) _log?.Warn("Sounds", $"Couldn't play a sound: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        });
    }

    // The file to hand the player: a tone's generated WAV, or the user's own file.
    private string? ResolvePath(string sound)
    {
        if (!SoundTones.IsTone(sound))
            return File.Exists(sound) ? sound : throw new FileNotFoundException($"no file at {sound}");

        // Written once per run rather than once ever, so a tone that changes between
        // versions replaces the copy an older build left behind.
        string path = Path.Combine(_tonesDir, sound[SoundTones.Prefix.Length..] + ".wav");
        lock (_toneLock)
        {
            if (_tonesWritten.Contains(sound)) return path;
            if (SoundTones.Render(sound) is not { } wav) return null;
            Directory.CreateDirectory(_tonesDir);
            File.WriteAllBytes(path, wav);
            _tonesWritten.Add(sound);
        }
        return path;
    }

    private void Output(string path, double volume)
    {
        if (OperatingSystem.IsWindows()) PlayWindows(path, volume);
        else if (OperatingSystem.IsMacOS()) Run("afplay", "-v", Number(volume), path);
        else if (_linuxPlayer.Value is { } player)
        {
            switch (player.Kind)
            {
                case LinuxPlayer.PipeWire: Run(player.Exe, "--volume", Number(volume), path); break;
                case LinuxPlayer.Pulse: Run(player.Exe, "--volume=" + (int)(volume * 65536), path); break;
                // aplay has no volume switch: the sound plays at the system level.
                default: Run(player.Exe, "-q", path); break;
            }
        }
        else throw new InvalidOperationException("no pw-play, paplay or aplay on PATH");
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static (string, LinuxPlayer)? FindLinuxPlayer()
    {
        string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach ((string name, LinuxPlayer kind) in new[]
                 {
                     ("pw-play", LinuxPlayer.PipeWire), ("paplay", LinuxPlayer.Pulse), ("aplay", LinuxPlayer.Alsa),
                 })
            foreach (string dir in dirs)
            {
                string exe = Path.Combine(dir, name);
                if (File.Exists(exe)) return (exe, kind);
            }
        return null;
    }

    // Start the player and wait for it on this pool thread, so the process is
    // reaped and counted against MaxConcurrent for as long as it sounds.
    private static void Run(string exe, params string[] args)
    {
        ProcessStartInfo info = new(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using Process? process = Process.Start(info);
        if (process is null) return;
        if (!process.WaitForExit(PlayerTimeout))
        {
            process.Kill();
            return;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} couldn't play {Path.GetFileName(args[^1])}");
    }

    // Windows: the MCI string interface plays WAV and MP3 with a per-sound volume.
    // The alias must be opened, played and closed on one thread; "play … wait"
    // holds this pool thread until the sound ends.
    private static void PlayWindows(string path, double volume)
    {
        string alias = "mudplay" + Interlocked.Increment(ref _windowsAlias);
        if (mciSendString($"open \"{path}\" type mpegvideo alias {alias}", IntPtr.Zero, 0, IntPtr.Zero) != 0)
            throw new InvalidOperationException("the sound file couldn't be opened");
        try
        {
            mciSendString($"setaudio {alias} volume to {(int)(volume * 1000)}", IntPtr.Zero, 0, IntPtr.Zero);
            mciSendString($"play {alias} wait", IntPtr.Zero, 0, IntPtr.Zero);
        }
        finally
        {
            mciSendString($"close {alias}", IntPtr.Zero, 0, IntPtr.Zero);
        }
    }

    private static int _windowsAlias;

    // Classic DllImport, like the rest of the interop here.
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, IntPtr returnString, int returnLength, IntPtr callback);
}
