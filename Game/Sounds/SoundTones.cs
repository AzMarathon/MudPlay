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
    // ones a swell.
    private readonly record struct Note(
        double Start, double Hz, double Length, double Level, double Decay,
        double Attack = 0.004, double Release = 0.01);

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

    // A level-up "ding", about four seconds: a deep boom that swells twice and dies
    // away slowly, a short strike at the start, and a bright shimmer that rises in
    // over the first half second and hangs above it. Built in layers; each layer is
    // one loudness curve shared by a handful of pitches.
    private static readonly Note[] LevelUpDing = new[]
    {
        // The boom. Every layer sits on the same 43 Hz so they add rather than cancel:
        // the opening swell, the larger second surge, and the long tail under it.
        Layer(0.00, 0.06, 0, 1.25, 0.30, 0.41, (43, 1), (86, 0.45), (89, 0.35)),
        Layer(1.00, 0.25, 0, 1.20, 0.45, 0.24, (43, 1), (86, 0.45), (145, 0.4)),
        Layer(1.00, 0.40, 0.55, 2.70, 1.20, 0.54, (43, 1), (86, 0.45)),

        // The rumble over it. Neighbouring pitches a few Hz apart beat against each other.
        Layer(0.00, 0.10, 1.6, 4.00, 0.80, 0.505, (145, 1), (161, 0.9), (243, 0.25)),
        Layer(0.15, 0.10, 1.6, 4.00, 0.80, 0.406, (143.2, 1), (156, 0.7), (272, 0.3)),
        Layer(0.45, 0.30, 1.2, 3.86, 0.80, 0.314, (146.5, 1), (162.6, 0.6), (218, 0.35)),

        // The strike, then the mid swell behind it.
        Layer(0.00, 0.006, 7, 1.00, 0.30, 0.21,
            (312, 0.7), (377, 0.8), (415, 1), (441, 0.55), (560, 0.4), (614, 0.45), (716, 0.4), (775, 0.4)),
        Layer(0.00, 0.006, 1, 1.00, 0.30, 0.06, (900, 1), (1150, 0.8), (1470, 0.7)),
        Layer(0.00, 0.10, 1.6, 3.00, 0.80, 0.09, (350, 1), (382, 0.5), (431, 0.5)),
        Layer(0.70, 0.30, 1.6, 3.30, 0.80, 0.19, (349, 1), (447, 0.7), (538, 0.4), (760, 0.5), (810, 0.45)),
        Layer(0.45, 0.50, 2.2, 3.00, 0.80, 0.214, (813, 0.8), (1308, 0.7), (1470, 1), (1577, 0.6)),
        Layer(1.00, 0.10, 1.6, 2.50, 0.80, 0.086, (1265, 1), (1954, 0.7)),

        // The shimmer: high bell-like pitches that come in as three waves.
        Layer(0.12, 0.45, 1.2, 4.10, 0.80, 0.24, (2939, 1), (3494, 0.8), (3305, 0.45), (4048, 0.45)),
        Layer(0.80, 0.50, 2.2, 3.50, 0.80, 0.189, (2342, 0.8), (2573, 0.6), (4409, 0.8), (4592, 0.9), (4775, 1)),
        Layer(0.60, 0.10, 1.6, 3.70, 0.80, 0.085, (3133, 1), (3623, 1)),
        Layer(0.45, 0.30, 1.2, 3.86, 0.80, 0.119, (5329, 1), (6062, 0.6), (7935, 0.4)),
        Layer(0.15, 0.60, 2.2, 4.10, 0.80, 0.19, (5146, 0.8), (5378, 0.7), (7100, 0.5), (8554, 0.5), (9593, 0.4)),
    }.SelectMany(static layer => layer).ToArray();

    // Several pitches sharing one loudness curve. Level is the layer's as a whole:
    // it is split between the pitches by weight so their combined power matches a
    // single note at that level.
    private static Note[] Layer(
        double start, double attack, double decay, double length, double release, double level,
        params (double Hz, double Weight)[] pitches)
    {
        double norm = Math.Sqrt(pitches.Sum(static p => p.Weight * p.Weight));
        return pitches
            .Select(p => new Note(start, p.Hz, length, level * p.Weight / norm, decay, attack, release))
            .ToArray();
    }

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
                double phase = 2 * Math.PI * n.Hz * t;
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
