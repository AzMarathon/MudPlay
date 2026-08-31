using System.Reflection;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Pins <see cref="GroundItemTracker"/>: the "You notice &lt;list&gt; here."
/// survey becomes a per-room, cash-filtered snapshot of floor loot. A fresh
/// survey supersedes the prior list, a wrapped survey stitches across two
/// rows, and a room change clears the snapshot.
/// </summary>
public sealed class GroundItemTrackerTests
{
    private static (GroundItemTracker ground, MessageRouter router, LineExtractor lines) Setup(
        Func<string, bool>? isKnownItem = null)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(), isKnownItem);
        LineExtractor lines = new(new TerminalEmulator(80, 24));
        ground.AttachLineExtractor(lines);
        return (ground, router, lines);
    }

    // Single-line survey path — through the router pattern subscription.
    private static void FeedRoom(MessageRouter router, string text) =>
        router.Dispatch(new LineExtractor.EmittedLine(
            text, Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

    // Multi-line stitch path — through the LineExtractor's emitted-line event.
    private static void FeedLine(LineExtractor lines, string text)
    {
        FieldInfo? field = typeof(LineExtractor).GetField(
            "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(lines) is Action<LineExtractor.EmittedLine> handler)
        {
            handler(new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }
    }

    [Fact]
    public void EmptyBeforeSurvey()
    {
        var (ground, _, _) = Setup();
        Assert.Empty(ground.Items);
    }

    [Fact]
    public void SingleLineSurvey_KeepsItems_DropsCash()
    {
        var (ground, router, _) = Setup();

        FeedRoom(router, "You notice a rusty dagger, 5 gold crowns, "
                       + "a silver ring and 2 copper farthings here.");

        // "5 gold crowns" / "2 copper farthings" are cash — filtered out.
        Assert.Equal(new[] { "a rusty dagger", "a silver ring" }, ground.Items);
    }

    [Fact]
    public void SingularCashEntry_IsFiltered()
    {
        var (ground, router, _) = Setup();

        FeedRoom(router, "You notice a platinum piece and a torch here.");

        // "a platinum" is the singular cash form; "a torch" is a real item.
        Assert.Equal(new[] { "a torch" }, ground.Items);
    }

    [Fact]
    public void FreshSurvey_SupersedesPrior()
    {
        var (ground, router, _) = Setup();

        FeedRoom(router, "You notice a rusty dagger here.");
        FeedRoom(router, "You notice a healing potion and a shield here.");

        Assert.Equal(new[] { "a healing potion", "a shield" }, ground.Items);
    }

    [Fact]
    public void MultiLineWrap_StitchesAcrossRows()
    {
        var (ground, _, lines) = Setup();

        FeedLine(lines, "You notice a rusty dagger, a torch and a");
        FeedLine(lines, "healing potion here.");

        Assert.Equal(new[] { "a rusty dagger", "a torch", "a healing potion" }, ground.Items);
    }

    [Fact]
    public void MultiLineWrap_StitchesAcrossFourRows_CrowdedFloor()
    {
        // The stock spillover case (report stock-20260825-101612): a crowded
        // adjacent-room floor wraps a "You notice" survey across FOUR rows, with
        // count-prefixed items split at the wrap ("…, 2" + "padded boots, …"). Every
        // item the deathpile sweep needs must survive the stitch — a single-line
        // parse missed it entirely, so the sweep peeked but never walked.
        var (ground, _, lines) = Setup();

        FeedLine(lines, "You notice bone key, amethyst ring, demonhide sandals, silver bracelet, 2");
        FeedLine(lines, "padded boots, 2 padded pants, 2 silk robe, 2 silk cape, 2 bone charm, 2 padded");
        FeedLine(lines, "gloves, 2 padded helm, waterskin, sandals, skullcap, cotton gloves, cloth");
        FeedLine(lines, "pants, small sign here.");

        Assert.Contains("bone key", ground.Items);
        Assert.Contains("2 padded boots", ground.Items);   // count survived the wrap-join
        Assert.Contains("2 silk robe", ground.Items);
        Assert.Contains("2 padded helm", ground.Items);
        Assert.Contains("cloth pants", ground.Items);      // wrapped "cloth" + "pants"
        Assert.Contains("small sign", ground.Items);
    }

    [Fact]
    public void RoomChanged_ClearsSnapshot()
    {
        var (ground, router, _) = Setup();
        FeedRoom(router, "You notice a rusty dagger here.");

        ground.OnRoomChanged();

        Assert.Empty(ground.Items);
    }

    [Fact]
    public void Disposed_StopsTracking()
    {
        var (ground, router, _) = Setup();
        ground.Dispose();

        FeedRoom(router, "You notice a rusty dagger here.");

        Assert.Empty(ground.Items);
    }

    // ----- item-table tiebreaker (denomination-named items) -----------

    // A stacked item whose name starts with a denomination word ("2 gold key")
    // has the same "N <denom> ..." shape as a coin pile. The count+denomination
    // heuristic alone can't tell them apart; the item-table predicate settles it —
    // "gold key" resolves to a real item, so the entry stays loot, not cash.
    [Fact]
    public void DenominationNamedStackedItem_StaysItem_WithItemTable()
    {
        // Table knows "gold key" but not the coin noun "gold crown".
        var (ground, router, _) = Setup(isKnownItem: KnownItems("gold key"));

        FeedRoom(router, "You notice 2 gold key, 50 gold crowns here.");

        // "2 gold key" survives (real item); "50 gold crowns" filtered as cash.
        Assert.Equal(new[] { "2 gold key" }, ground.Items);
    }

    // The tiebreaker must not swallow a genuine coin pile: a true "N <denom> ..."
    // that ISN'T in the item table still reads as cash and is filtered.
    [Fact]
    public void CoinPile_NotInItemTable_StillFilteredAsCash()
    {
        var (ground, router, _) = Setup(isKnownItem: KnownItems("gold key"));

        FeedRoom(router, "You notice 50 gold crowns and a torch here.");

        Assert.Equal(new[] { "a torch" }, ground.Items);
    }

    // Singular denomination-named item ("a copper key") is already kept by the
    // "ends in piece(s)" rule, but the item-table override keeps it robust even
    // when the wording doesn't end in the coin noun.
    [Fact]
    public void SingularDenominationNamedItem_StaysItem_WithItemTable()
    {
        var (ground, router, _) = Setup(isKnownItem: KnownItems("copper key"));

        FeedRoom(router, "You notice a copper key and a platinum piece here.");

        // "a copper key" is a real item; "a platinum piece" is singular cash.
        Assert.Equal(new[] { "a copper key" }, ground.Items);
    }

    // Without an item table wired (no game data), the heuristic stands alone and
    // the denomination-named stacked item is misread as cash — documents the
    // fallback the injected predicate exists to fix.
    [Fact]
    public void DenominationNamedStackedItem_MisreadAsCash_WithoutItemTable()
    {
        var (ground, router, _) = Setup();

        FeedRoom(router, "You notice 2 gold key here.");

        Assert.Empty(ground.Items);
    }

    // ----- compound item names containing " and " (report 20260825-172400) --

    // An item whose own canonical name contains "and" ("rope and grapple", MDB
    // #191) must survive as one entry, not split at its internal "and" the way
    // a genuine list separator would be.
    [Fact]
    public void CompoundItemName_SoleFloorItem_StaysWhole_WithItemTable()
    {
        var (ground, router, _) = Setup(isKnownItem: KnownItems("rope and grapple"));

        FeedRoom(router, "You notice a rope and grapple here.");

        Assert.Equal(new[] { "a rope and grapple" }, ground.Items);
    }

    // Comma already separates it from an unrelated item — the and-split still
    // must not break the compound name's own internal "and".
    [Fact]
    public void CompoundItemName_AfterComma_StaysWhole_WithItemTable()
    {
        var (ground, router, _) = Setup(isKnownItem: KnownItems("rope and grapple"));

        FeedRoom(router, "You notice a torch, a rope and grapple here.");

        Assert.Equal(new[] { "a torch", "a rope and grapple" }, ground.Items);
    }

    // The 2-item list form ("X and Y", no comma) where Y is itself a compound
    // name — naive splitting sees three " and "-delimited pieces; the merge
    // pass must recover exactly the two real items.
    [Fact]
    public void CompoundItemName_AsSecondOfTwoItems_StaysWhole_WithItemTable()
    {
        var (ground, router, _) = Setup(isKnownItem: KnownItems("rope and grapple"));

        FeedRoom(router, "You notice a torch and a rope and grapple here.");

        Assert.Equal(new[] { "a torch", "a rope and grapple" }, ground.Items);
    }

    // Two genuinely separate items joined by the real "and" separator must NOT
    // be merged just because they're adjacent — only a pairing that itself
    // resolves to a known item is ever rejoined.
    [Fact]
    public void TwoRealItems_JoinedByAnd_AreNotIncorrectlyMerged()
    {
        var (ground, router, _) = Setup(isKnownItem: KnownItems("torch", "dagger"));

        FeedRoom(router, "You notice a torch and a dagger here.");

        Assert.Equal(new[] { "a torch", "a dagger" }, ground.Items);
    }

    // Without an item table wired (no game data), the merge heuristic has
    // nothing to check against and the naive split stands alone — documents
    // the fallback the injected predicate exists to fix, same pattern as the
    // cash-tiebreaker fallback test above.
    [Fact]
    public void CompoundItemName_MisparsedIntoTwoEntries_WithoutItemTable()
    {
        var (ground, router, _) = Setup();

        FeedRoom(router, "You notice a rope and grapple here.");

        Assert.Equal(new[] { "a rope", "grapple" }, ground.Items);
    }

    // Test double for the item table: matches an entry to a known item name after
    // the same article/count stripping the real ItemNameStore.Normalize applies.
    private static Func<string, bool> KnownItems(params string[] names)
    {
        HashSet<string> set = new(names, StringComparer.OrdinalIgnoreCase);
        return entry =>
        {
            string[] words = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int skip = 0;
            if (words.Length > 0 && (words[0] is "a" or "an" or "the" or "some"
                    || int.TryParse(words[0], out _)))
                skip = 1;
            string key = string.Join(' ', words.Skip(skip));
            return set.Contains(key);
        };
    }
}
