namespace MudPlay.Services;

// A MegaMUD character file (the `.ini` beside its data folder): `[Section]` headers
// and `Key=Value` lines. Sections and keys are matched without regard to case, and
// a value keeps everything after the first `=` as written. Combat, Health and Spells
// come as a base section plus one `.P<n>` section per MegaMUD profile, each a whole
// copy of the base with that profile's values.
public sealed class MegaMudIni
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public static MegaMudIni Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ini = new MegaMudIni();
        Dictionary<string, string>? section = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line[0] == '[' && line.IndexOf(']') is var close and > 1)
            {
                string name = line[1..close].Trim();
                if (!ini._sections.TryGetValue(name, out section))
                    ini._sections[name] = section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (section is null || eq <= 0) continue;
            section[line[..eq].Trim()] = line[(eq + 1)..];
        }
        return ini;
    }

    public bool HasSection(string section) => _sections.ContainsKey(section);

    public IReadOnlyCollection<string> Keys(string section) =>
        _sections.TryGetValue(section, out Dictionary<string, string>? s) ? s.Keys : Array.Empty<string>();

    // The value as written, or null when the section or key isn't there.
    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out Dictionary<string, string>? s) && s.TryGetValue(key, out string? v) ? v : null;
}
