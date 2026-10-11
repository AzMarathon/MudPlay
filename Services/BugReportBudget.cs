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

        // The masking that follows writes `[redacted]` over values that may be
        // shorter than it: a little is kept back so the result still fits.
        maxBytes -= maxBytes / 50;
        sections = CapStaticBodies(sections, maxBytes, overheadBytes);

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

    // The share of the budget the sections with no history may take between
    // them. Past it the largest of them are cut down first: one runaway static
    // section (a thousand overrides, a huge workshop) must not cost the report
    // its program log.
    private const int StaticSharePercent = 60;

    private static IReadOnlyList<BugReportBuilder.Section> CapStaticBodies(
        IReadOnlyList<BugReportBuilder.Section> sections, int maxBytes, int overheadBytes)
    {
        long target = (long)maxBytes * StaticSharePercent / 100 - overheadBytes;
        int[] sizes = new int[sections.Count];
        long total = 0;
        for (int i = 0; i < sections.Count; i++)
        {
            if (!sections[i].InBudget) continue;
            sizes[i] = Encoding.UTF8.GetByteCount(sections[i].Body);
            total += sizes[i];
        }
        if (total <= target) return sections;

        // The largest cap that brings the bodies' sum to the target: every body
        // under it is whole, every body over it is cut to it.
        int[] ordered = sizes.Where(s => s > 0).OrderBy(s => s).ToArray();
        long cap = 0, below = 0;
        for (int i = 0; i < ordered.Length; i++)
        {
            long candidate = (target - below) / (ordered.Length - i);
            if (candidate <= ordered[i]) { cap = Math.Max(candidate, MinCappedBody); break; }
            below += ordered[i];
            cap = ordered[i];
        }

        List<BugReportBuilder.Section> capped = new(sections.Count);
        for (int i = 0; i < sections.Count; i++)
        {
            BugReportBuilder.Section s = sections[i];
            if (!s.InBudget || sizes[i] <= cap) { capped.Add(s); continue; }
            // Cut at a line end, on a character boundary, at or before the cap.
            int chars = (int)Math.Min(s.Body.Length, cap * s.Body.Length / sizes[i]);
            int lineEnd = s.Body.LastIndexOf('\n', Math.Max(0, chars - 1));
            if (lineEnd > chars / 2) chars = lineEnd;
            string kept = s.Body[..chars];
            // A code fence left open would swallow the rest of the report.
            bool openFence = kept.Split("```").Length % 2 == 0;
            capped.Add(s with
            {
                Body = kept + (openFence ? "\n```" : string.Empty)
                    + $"\n\n_(the rest of this section, {(sizes[i] - Encoding.UTF8.GetByteCount(kept)) / 1024} KB of {sizes[i] / 1024} KB, "
                    + $"is left out to keep the report under {MaxBytes / 1000} KB)_",
            });
        }
        return capped;
    }

    private const int MinCappedBody = 2_000;

    // The heading marks, blank lines and code fence written around one section.
    private const int SectionOverhead = 20;

    // What a trimmed section's heading says after its name.
    public static string TrimNote(int trimmed) =>
        trimmed <= 0 ? string.Empty
        : $" — {trimmed} oldest {(trimmed == 1 ? "entry" : "entries")} left out to keep the report under {MaxBytes / 1000} KB";
}
