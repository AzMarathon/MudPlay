using System.Text;

namespace MudPlay.Services;

// Keeps a rendered bug report under its size budget. Reports are attached to
// issues and read whole; past about a megabyte they stop being either.
//
// Only the history sections give way (the program log, the command trail, the
// scrollback): the oldest entries of whichever is largest for its weight go
// first, since the incident is at the end of each. The wire sections are not
// counted and not trimmed: the user chose to include them.
public static class BugReportBudget
{
    // The whole report without the wire sections.
    public const int MaxBytes = 1_000_000;

    // A history section is never trimmed below this many entries: the newest few
    // are worth more than anything else in the report.
    public const int KeepAtLeast = 50;

    public readonly record struct Result(IReadOnlyList<BugReportBuilder.Section> Sections, int Bytes, bool OverBudget);

    // overheadBytes is what the caller writes around the sections (title, issue
    // text).
    public static Result Fit(IReadOnlyList<BugReportBuilder.Section> sections, int maxBytes = MaxBytes, int overheadBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(sections);

        int count = sections.Count;
        int[][] entryBytes = new int[count][];
        long[] remaining = new long[count];   // bytes of the entries still kept
        int[] dropped = new int[count];
        long total = overheadBytes;
        for (int i = 0; i < count; i++)
        {
            BugReportBuilder.Section s = sections[i];
            if (!s.InBudget) { entryBytes[i] = []; continue; }
            total += Encoding.UTF8.GetByteCount(s.Heading) + Encoding.UTF8.GetByteCount(s.Body) + SectionOverhead;
            if (s.Entries is not { Count: > 0 } entries) { entryBytes[i] = []; continue; }
            int[] sizes = new int[entries.Count];
            for (int e = 0; e < sizes.Length; e++)
            {
                sizes[e] = Encoding.UTF8.GetByteCount(entries[e]) + 1;
                remaining[i] += sizes[e];
            }
            entryBytes[i] = sizes;
            total += remaining[i];
        }

        while (total > maxBytes)
        {
            int pick = -1;
            double pickWeighted = 0;
            for (int i = 0; i < count; i++)
            {
                if (entryBytes[i].Length - dropped[i] <= KeepAtLeast) continue;
                double weighted = remaining[i] / (double)Math.Max(1, sections[i].BudgetWeight);
                if (weighted > pickWeighted) { pick = i; pickWeighted = weighted; }
            }
            if (pick < 0) break;   // nothing left that may be trimmed
            int size = entryBytes[pick][dropped[pick]];
            dropped[pick]++;
            remaining[pick] -= size;
            total -= size;
        }

        List<BugReportBuilder.Section> fitted = new(count);
        for (int i = 0; i < count; i++)
        {
            BugReportBuilder.Section s = sections[i];
            fitted.Add(dropped[i] == 0
                ? s
                : s with
                {
                    Entries = s.Entries!.Skip(dropped[i]).ToList(),
                    TrimmedEntries = s.TrimmedEntries + dropped[i],
                });
        }
        return new Result(fitted, (int)Math.Min(int.MaxValue, total), total > maxBytes);
    }

    // The heading marks, blank lines and code fence written around one section.
    private const int SectionOverhead = 20;

    // What a trimmed section's heading says after its name.
    public static string TrimNote(int trimmed) =>
        trimmed <= 0 ? string.Empty
        : $" — {trimmed} oldest {(trimmed == 1 ? "entry" : "entries")} left out to keep the report under {MaxBytes / 1000} KB";
}
