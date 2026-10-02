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

    // One struck note: when it starts, its pitch, how long it rings and how hard.
    // Decay is how fast it dies away (higher = shorter, more bell-like).
    private readonly record struct Note(double Start, double Hz, double Length, double Level, double Decay);

    // The tone as a 16-bit mono WAV file, or null for a name that isn't a tone.
    public static byte[]? Render(string tone)
    {
        Note[]? notes = tone switch
        {
            Ding => BellDing,
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

    // One strike of a small bell: a hum an octave below, the note itself (with a
    // faint second pitch just beside it, for the slow waver of a real bell), and
    // overtones above. The upper two aren't whole multiples of the note, which is
    // what tells the ear "bell" rather than "beep". Each overtone is quieter and
    // dies sooner than the one below, so the strike is bright and the ring mellow.
    private static readonly Note[] BellDing =
    {
        new(0, 523.3, 1.8, 0.22, 2.5),
        new(0, 1046.5, 1.8, 0.60, 3.0), new(0, 1048.0, 1.8, 0.12, 3.0),
        new(0, 2093.0, 1.2, 0.30, 5.0),
        new(0, 2888.3, 0.8, 0.16, 8.0),
        new(0, 5651.1, 0.4, 0.05, 14.0),
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
                // A 4 ms attack and a short release keep the note from clicking.
                double attack = Math.Min(1, t / 0.004), release = Math.Min(1, (n.Length - t) / 0.01);
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
