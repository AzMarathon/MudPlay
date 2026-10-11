using System.Text;
using System.Text.RegularExpressions;

namespace MudPlay.Services;

// Takes the account's login name, login password and suicide password out of a
// bug report. Reports get attached to public issues, so none of the three may
// appear anywhere in one: not in the scrollback, the wire, the program log, the
// sent-command trail, a trigger's text or a settings dump. The scrub runs over
// the whole rendered document, after every section is built, so a section added
// later is covered without knowing about it.
//
// A match is case-insensitive and may be broken by a line break (a row the
// terminal wrapped, with the next row's time stamp in front of it).
//
// Two things are left alone, and the report says so for each:
//   - a value shorter than MinLength, because masking every "ab" would shred the
//     report;
//   - a login name that is also the character's name in the game, which every
//     player can see and which the report can't do without.
public static class BugReportScrub
{
    public const string Mask = "[redacted]";

    // What replaces a whole trigger / alias / macro definition that holds one of
    // the three.
    public const string OmittedDefinition = "[omitted: contains account details]";

    public const int MinLength = 3;

    // One value to mask, and the name the report uses for it in a note.
    public readonly record struct Secret(string Label, string? Value);

    // What may sit between two characters of a value that a terminal row wrapped:
    // the line break, then the next row's time stamp or its blank padding.
    private const string RowBreak = @"(?:\r?\n(?:\d\d:\d\d:\d\d(?:\.\d{3})?)?[ ]{0,10})?";

    public sealed class Scrubber
    {
        public static readonly Scrubber None = new(Array.Empty<Regex>(), Array.Empty<string>(), Array.Empty<string>());

        private readonly Regex[] _patterns;
        private readonly string[] _values;

        internal Scrubber(Regex[] patterns, string[] values, IReadOnlyList<string> notes)
        {
            _patterns = patterns;
            _values = values;
            Notes = notes;
        }

        // What was left unmasked and why, for the Session section. Never holds a
        // value.
        public IReadOnlyList<string> Notes { get; }

        public bool HasTerms => _patterns.Length > 0;

        public string Apply(string text) => Apply(text, out _);

        public string Apply(string text, out int replaced)
        {
            replaced = 0;
            if (string.IsNullOrEmpty(text) || _patterns.Length == 0) return text;
            int count = 0;
            foreach (Regex pattern in _patterns)
            {
                text = pattern.Replace(text, m =>
                {
                    count++;
                    // A match that swallowed a row break keeps the break, so the
                    // rows after it stay rows.
                    return m.Value.Contains('\n') ? Mask + "\n" : Mask;
                });
            }
            replaced = count;
            return text;
        }

        // Whether the text holds one of the values, for a definition that is left
        // out whole rather than masked in part.
        public bool Contains(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (string value in _values)
                if (text.Contains(value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // A record that holds one must not print the values with its members.
        public override string ToString() => "(account-detail scrubber)";
    }

    // publicNames are the character's names as the game shows them: a login name
    // equal to one of them is not masked.
    public static Scrubber Build(IEnumerable<Secret> secrets, IEnumerable<string?> publicNames)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(publicNames);

        HashSet<string> shown = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? name in publicNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string trimmed = name.Trim();
            shown.Add(trimmed);
            // The stat sheet prints "First Last"; the login is matched against
            // the first name too, which is what the game shows everywhere else.
            int space = trimmed.IndexOf(' ');
            if (space > 0) shown.Add(trimmed[..space]);
        }

        List<Regex> patterns = new();
        List<string> values = new();
        List<string> notes = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (Secret secret in secrets)
        {
            string? value = secret.Value?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (value.Length < MinLength)
            {
                notes.Add($"the {secret.Label} is shorter than {MinLength} characters and is not masked "
                    + "(masking it would blank ordinary text throughout)");
                continue;
            }
            if (secret.Label == LoginNameLabel && shown.Contains(value))
            {
                notes.Add("the login name is the same as the character's name, which every player sees, and is not masked");
                continue;
            }
            if (!seen.Add(value)) continue;
            values.Add(value);
            patterns.Add(new Regex(SplitTolerant(value),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
        }
        // Longest first: a password that contains the login name is masked whole,
        // not left with its tail showing.
        Regex[] ordered = patterns.OrderByDescending(p => p.ToString().Length).ToArray();
        return new Scrubber(ordered, values.ToArray(), notes);
    }

    public const string LoginNameLabel = "login name";
    public const string LoginPasswordLabel = "login password";
    public const string SuicidePasswordLabel = "suicide password";

    private static string SplitTolerant(string value)
    {
        StringBuilder sb = new(value.Length * (RowBreak.Length + 2));
        for (int i = 0; i < value.Length; i++)
        {
            if (i > 0) sb.Append(RowBreak);
            sb.Append(Regex.Escape(value[i].ToString()));
        }
        return sb.ToString();
    }
}
