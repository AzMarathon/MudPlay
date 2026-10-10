using System.Reflection;
using System.Text.RegularExpressions;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A template's regex is tried from every position of a line and its wildcards stretch
// to the line's end from each, so on a line it doesn't match its time grows with the
// square of the line's length or worse. A search's reply is one unbroken line however
// long the floor list is, and the cast-line templates spent seconds refusing a
// 2,200-character one (report paradigm-20261009-164508). The matcher now walks the
// template's literal text along the line first. These pin the two things that walk
// has to be: never wrong about a line the regex accepts, and enough to keep the regex
// off a floor list. Both run against the shipped catalogues, where the templates are.
public sealed class CasterMessageMatcherLongLineTests : IDisposable
{
    private readonly string _dir;

    public CasterMessageMatcherLongLineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-longline-" + Path.GetRandomFileName());
        AppPaths.ExtractEmbeddedSeeds(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    private static readonly FieldInfo RegexField =
        typeof(CasterMessageMatcher).GetField("_regex", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // Every distinct templated wording in both realms' catalogues, compiled the way
    // the bulk recognizers compile them.
    private List<CasterMessageMatcher> CatalogueMatchers()
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<CasterMessageMatcher> matchers = new();
        foreach (string seed in new[] { "Messages.stock.seed.json", "Messages.paradigm.seed.json" })
        {
            foreach (MessageRecord r in JsonStore.Load<List<MessageRecord>>(Path.Combine(_dir, seed))
                                         ?? new List<MessageRecord>())
            {
                foreach (string? slot in new[] { r.CasterMessage, r.TargetMessage, r.AppliedMessage, r.AppliedEndsWith })
                    Add(slot);
                if (!MessageRecord.IsBlankOrAbsent(r.WitnessMessage))
                    foreach (string wording in r.WitnessMessage.Split('\n')) Add(wording);
            }
        }
        return matchers;

        void Add(string? template)
        {
            if (MessageRecord.IsBlankOrAbsent(template) || !template!.Contains('{')) return;
            if (!seen.Add(template.Trim())) return;
            if (CasterMessageMatcher.TryCreate(template.Trim(), compiled: false) is { } matcher)
                matchers.Add(matcher);
        }
    }

    // The line a template describes, with something plausible in each placeholder.
    private static string Filled(string template) => template
        .Replace("{spellname}", "minor healing")
        .Replace("{target}", "Raijin")
        .Replace("{source}", "Suijin")
        .Replace("{damage}", "42").Replace("{dmg}", "42").Replace("{d}", "42")
        .Replace("{s}", "the orc");

    private static readonly string[] OrdinaryLines =
    {
        "You notice 2 wooden skiff, 2 rope and grapple, scorpion tail here.",
        "Also here: bronze guardian, bronze guardian, bronze spellbreaker.",
        "Obvious exits: north, south, open door east",
        "You cast bless on Raijin!",
        "Raijin casts minor healing on Suijin!",
        "The orc swings at you with a rusty sword, but misses!",
        "You gain 825 experience.",
        "You feel better.",
        "The door is now open.",
        "You", "The", "!", ".", " ", "a b", "!!",
    };

    [Fact]
    public void TheLiteralWalk_NeverRefusesALineTheRegexAccepts()
    {
        List<CasterMessageMatcher> matchers = CatalogueMatchers();
        Assert.True(matchers.Count > 500, $"expected hundreds of catalogue templates, got {matchers.Count}");

        List<string> lines = new(OrdinaryLines);
        // A spread of other templates' own lines, so near-misses are tried too.
        for (int i = 0; i < matchers.Count; i += 40) lines.Add(Filled(matchers[i].Template));

        int ownLineMatched = 0;
        foreach (CasterMessageMatcher matcher in matchers)
        {
            Regex regex = (Regex)RegexField.GetValue(matcher)!;
            string own = Filled(matcher.Template);
            if (regex.IsMatch(own)) ownLineMatched++;

            foreach (string line in lines.Append(own).Append($"[HP=10/MA=5]:{own} trailing"))
            {
                bool regexAccepts = regex.IsMatch(line);
                Assert.True(!regexAccepts || matcher.LiteralRunsOccurInOrder(line),
                    $"the walk refused a line its regex accepts: template '{matcher.Template}', line '{line}'");
                Assert.Equal(regexAccepts, matcher.TryMatch(line, out _));
            }
        }
        // Guards the vacuous case: the templates really do match the lines built from them.
        Assert.True(ownLineMatched > matchers.Count * 9 / 10,
            $"only {ownLineMatched} of {matchers.Count} templates matched their own line");
    }

    // The count of regexes run is what the freeze was made of, so it is what's pinned:
    // a long floor list reaches a handful of the catalogue's regexes at most, and
    // only ones that accept it. Accepting is one pass along the line; it is the
    // refusal that tries every start.
    [Theory]
    [InlineData(116)]
    [InlineData(2000)]
    public void AFloorList_ReachesOnlyRegexesThatAcceptIt(int stacks)
    {
        string floor = HugeFloor.Line(stacks);
        List<CasterMessageMatcher> matchers = CatalogueMatchers();

        List<CasterMessageMatcher> reached = matchers.Where(m => m.LiteralRunsOccurInOrder(floor)).ToList();

        Assert.True(reached.Count <= 5,
            $"{reached.Count} of {matchers.Count} templates would run their regex over a "
            + $"{floor.Length}-character floor list: " + string.Join(" | ", reached.Take(8).Select(m => m.Template)));
        foreach (CasterMessageMatcher matcher in reached)
            Assert.True(matcher.TryMatch(floor, out _),
                $"template '{matcher.Template}' gets past the walk and is then refused by its regex");
    }

    [Theory]
    [InlineData("{s} casts {s} on {s}!", "Raijin casts bless on Suijin!", true)]
    [InlineData("{s} casts {s} on {s}!", "Raijin casts bless on !", false)]          // nothing for the last name
    [InlineData("{s} casts {s} on {s}!", " casts bless on Suijin!", false)]          // nothing for the first
    [InlineData("{s} casts {s} on {s}!", "Raijin on Suijin casts bless!", false)]    // runs out of order
    [InlineData("You feel {s}", "You feel ", false)]                                  // closing placeholder left empty
    [InlineData("You feel {s}", "You feel X", true)]
    [InlineData("{s}{s}!", "ab!", true)]                                              // touching placeholders, one character each
    [InlineData("{s}{s}!", "a!", false)]
    public void TheLiteralWalk_ReadsEachPlaceholderAsAtLeastOneCharacter(string template, string line, bool fits)
    {
        CasterMessageMatcher matcher = CasterMessageMatcher.TryCreate(template)!;

        Assert.Equal(fits, matcher.LiteralRunsOccurInOrder(line));
        Assert.Equal(fits, matcher.TryMatch(line, out _));
    }
}
