using System;
using System.Text;

namespace MudPlay.Services.Update;

// Pulls a single version's entry out of CHANGELOG.md so the update window can show
// the actual "what's new" bullets instead of the hand-authored GitHub release body
// (which is publish boilerplate — the asset table + checksum instructions). Pure
// string work so it's unit-testable without a network fetch.
public static class ChangelogExtractor
{
    // The entry body for `version` (or the top-most entry when no version matches):
    // every line under the `## <version>` heading up to the next `## ` heading, with
    // the heading line itself and the internal `- bug reports addressed:` bookkeeping
    // tail dropped. Returns null when the text has no entry to show.
    public static string? TopEntry(string? changelogMarkdown, string? version = null)
    {
        if (string.IsNullOrWhiteSpace(changelogMarkdown)) return null;
        string[] lines = changelogMarkdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int start = -1;
        if (!string.IsNullOrWhiteSpace(version))
            start = FindHeading(lines, h => HeadingMatchesVersion(h, version!));
        if (start < 0)
            start = FindHeading(lines, _ => true);   // fall back to the first entry
        if (start < 0) return null;

        var sb = new StringBuilder();
        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (IsEntryHeading(line)) break;                 // next version's entry
            if (IsBugReportsLine(line)) continue;            // internal bookkeeping tail
            sb.Append(line).Append('\n');
        }

        string body = sb.ToString().Trim();
        return body.Length == 0 ? null : body;
    }

    private static int FindHeading(string[] lines, Func<string, bool> match)
    {
        for (int i = 0; i < lines.Length; i++)
            if (IsEntryHeading(lines[i]) && match(lines[i])) return i;
        return -1;
    }

    // A CHANGELOG version heading: "## 3.79.0". The top-level "# Version history"
    // title is a single '#', so requiring "## " excludes it.
    private static bool IsEntryHeading(string line) =>
        line.StartsWith("## ", StringComparison.Ordinal);

    private static bool HeadingMatchesVersion(string heading, string version)
    {
        string text = heading[3..].Trim();                  // after "## "
        string want = version.TrimStart('v', 'V');
        string got = text.TrimStart('v', 'V');
        return string.Equals(got, want, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBugReportsLine(string line) =>
        line.TrimStart().StartsWith("- bug reports addressed", StringComparison.OrdinalIgnoreCase);
}
