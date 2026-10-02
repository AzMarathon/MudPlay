using System.IO;

namespace MudPlay.Game.Sounds;

// The built-in sounds: short tones synthesized here, so every cue has something to
// play without shipping or licensing audio files. A sound setting names one as
// "tone:<name>"; anything else is a file path.
public static class SoundTones
{
    public const string Prefix = "tone:";
    public const string Ding = Prefix + "ding";
    public const string Chime = Prefix + "chime";
    public const string Fanfare = Prefix + "fanfare";
    public const string Coin = Prefix + "coin";
    public const string Alert = Prefix + "alert";
    public const string Alarm = Prefix + "alarm";
    public const string Low = Prefix + "low";
    public const string Click = Prefix + "click";

    // Id and the name the Sounds tab shows, in menu order.
    public static IReadOnlyList<(string Id, string Label)> All { get; } = new[]
    {
        (Ding, "Ding"), (Chime, "Chime"), (Fanfare, "Fanfare"), (Coin, "Coin"),
        (Alert, "Alert"), (Alarm, "Alarm"), (Low, "Low tone"), (Click, "Click"),
    };

    public static bool IsTone(string? sound) =>
        sound is not null && sound.StartsWith(Prefix, StringComparison.Ordinal);

    private const int SampleRate = 44100;

    // One note: when it starts, its pitch, how long it rings and how hard. Decay is
    // how fast it dies away (higher = shorter, more bell-like). Attack and Release
    // are the fade in and out, in seconds: the defaults are a struck note, longer
    // ones a swell. GlideTo, when set, slides the pitch there over the note's length.
    private readonly record struct Note(
        double Start, double Hz, double Length, double Level, double Decay,
        double Attack = 0.004, double Release = 0.01, double GlideTo = 0);

    // The tone as a 16-bit mono WAV file, or null for a name that isn't a tone.
    public static byte[]? Render(string tone)
    {
        Note[]? notes = tone switch
        {
            Ding => LevelUpDing,
            Chime => new[] { new Note(0, 1046.5, 0.5, 0.7, 6), new Note(0.14, 1568, 0.7, 0.7, 5) },
            Fanfare => new[]
            {
                new Note(0, 523.3, 0.22, 0.6, 9), new Note(0.11, 659.3, 0.22, 0.6, 9),
                new Note(0.22, 784, 0.22, 0.6, 9), new Note(0.33, 1046.5, 0.8, 0.75, 4),
            },
            Coin => new[] { new Note(0, 987.8, 0.09, 0.6, 14), new Note(0.08, 1318.5, 0.45, 0.7, 8) },
            Alert => new[] { new Note(0, 880, 0.13, 0.75, 3), new Note(0.2, 880, 0.13, 0.75, 3) },
            Alarm => new[]
            {
                new Note(0, 987.8, 0.15, 0.8, 2), new Note(0.18, 740, 0.15, 0.8, 2),
                new Note(0.36, 987.8, 0.15, 0.8, 2), new Note(0.54, 740, 0.22, 0.8, 2),
            },
            Low => new[] { new Note(0, 220, 0.3, 0.8, 5), new Note(0.25, 164.8, 0.6, 0.8, 4) },
            Click => new[] { new Note(0, 1500, 0.04, 0.6, 60) },
            _ => null,
        };
        return notes is null ? null : ToWav(Mix(notes));
    }

    // A level-up "ding": a rising swell that opens into a bright, slowly fading D
    // major chord with a few high sparkles on top. The slightly detuned copies of
    // three chord notes beat against them, which is what makes it shimmer.
    private static readonly Note[] LevelUpDing =
    {
        new(0, 293.7, 0.50, 0.20, 0, Attack: 0.32, Release: 0.16, GlideTo: 1174.7),
        new(0.05, 587.3, 0.45, 0.10, 0, Attack: 0.30, Release: 0.14, GlideTo: 2349.3),

        new(0.36, 146.8, 1.4, 0.22, 3.0, Attack: 0.02, Release: 0.15),
        new(0.36, 293.7, 1.4, 0.30, 2.0, Attack: 0.03, Release: 0.15),
        new(0.36, 587.3, 1.4, 0.34, 2.2, Attack: 0.03, Release: 0.15),
        new(0.36, 880.0, 1.4, 0.26, 2.4, Attack: 0.03, Release: 0.15),
        new(0.36, 1174.7, 1.4, 0.30, 2.6, Attack: 0.03, Release: 0.15),
        new(0.36, 1480.0, 1.4, 0.20, 3.0, Attack: 0.03, Release: 0.15),
        new(0.36, 1760.0, 1.4, 0.14, 3.4, Attack: 0.03, Release: 0.15),
        new(0.36, 589.6, 1.4, 0.15, 2.2, Attack: 0.03, Release: 0.15),
        new(0.36, 884.4, 1.4, 0.10, 2.4, Attack: 0.03, Release: 0.15),
        new(0.36, 1170.0, 1.4, 0.14, 2.6, Attack: 0.03, Release: 0.15),

        new(0.50, 2349.3, 0.5, 0.10, 7), new(0.62, 2960.0, 0.5, 0.08, 7),
        new(0.74, 3520.0, 0.5, 0.07, 7), new(0.86, 4698.6, 0.5, 0.05, 8),
    };

    private static short[] Mix(Note[] notes)
    {
        double end = 0;
        foreach (Note n in notes) end = Math.Max(end, n.Start + n.Length);
        double[] mix = new double[(int)(end * SampleRate) + 1];
        foreach (Note n in notes)
        {
            int first = (int)(n.Start * SampleRate), count = (int)(n.Length * SampleRate);
            for (int i = 0; i < count; i++)
            {
                double t = i / (double)SampleRate;
                // Even a struck note fades in and out over a few ms, so it never clicks.
                double attack = Math.Min(1, t / n.Attack), release = Math.Min(1, (n.Length - t) / n.Release);
                double envelope = attack * release * Math.Exp(-n.Decay * t);
                double phase = 2 * Math.PI * Phase(n, t);
                double wave = Math.Sin(phase) + 0.25 * Math.Sin(2 * phase);
                mix[first + i] += n.Level * envelope * wave / 1.25;
            }
        }

        // A chord of many notes can sum past full scale: bring the whole sound down
        // rather than clip its peaks.
        double peak = 0;
        foreach (double sample in mix) peak = Math.Max(peak, Math.Abs(sample));
        double scale = peak > 1 ? 1 / peak : 1;

        short[] pcm = new short[mix.Length];
        for (int i = 0; i < mix.Length; i++)
            pcm[i] = (short)(Math.Clamp(mix[i] * scale, -1, 1) * short.MaxValue * 0.9);
        return pcm;
    }

    // Cycles elapsed t seconds into the note. A gliding note's pitch moves by equal
    // musical steps, so its cycle count is the integral of that exponential curve.
    private static double Phase(Note n, double t)
    {
        if (n.GlideTo <= 0 || n.GlideTo == n.Hz) return n.Hz * t;
        double ratio = n.GlideTo / n.Hz;
        return n.Hz * n.Length / Math.Log(ratio) * (Math.Pow(ratio, t / n.Length) - 1);
    }

    private static byte[] ToWav(short[] pcm)
    {
        using MemoryStream stream = new();
        using BinaryWriter w = new(stream);
        int dataBytes = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (short s in pcm) w.Write(s);
        w.Flush();
        return stream.ToArray();
    }
}
