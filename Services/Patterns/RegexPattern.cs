using System.Text.RegularExpressions;
using MudPlay.Terminal;

namespace MudPlay.Services.Patterns;

// Full Regex match against the line's text. Capture groups from index 1 onward
// are returned to the handler via MatchResult.Groups.
public sealed class RegexPattern : IMessagePattern
{
    private readonly Regex _regex;
    private static readonly string[] EmptyGroups = [];

    public string Id { get; }
    public int Priority { get; }

    // Construct with the pattern's stable id, the regex pattern string, and an
    // optional execution priority (higher fires first; default 0). options
    // supplies extra RegexOptions; Compiled and CultureInvariant are always
    // applied.
    public RegexPattern(string id, string pattern, int priority = 0, RegexOptions options = RegexOptions.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        Id = id;
        Priority = priority;
        _regex = Compiled.GetOrAdd(
            (pattern, options | RegexOptions.Compiled | RegexOptions.CultureInvariant),
            static key => new Regex(key.Pattern, key.Options));
    }

    // Compiling a regex is the expensive part of building a pattern, and the same
    // pattern text is built again for every router: once in the app, but once per
    // test in the test run, where the full default set was most of the run's time.
    // A Regex is immutable and safe to match on from any thread, so instances with
    // the same text and options share one.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex>
        Compiled = new();

    public bool TryMatch(LineExtractor.EmittedLine line, out MatchResult result)
    {
        Match m = _regex.Match(line.Text);
        if (!m.Success)
        {
            result = default;
            return false;
        }

        IReadOnlyList<string> groups = EmptyGroups;
        if (m.Groups.Count > 1)
        {
            string[] array = new string[m.Groups.Count - 1];
            for (int i = 1; i < m.Groups.Count; i++) array[i - 1] = m.Groups[i].Value;
            groups = array;
        }

        result = new MatchResult(Id, line, groups);
        return true;
    }
}
