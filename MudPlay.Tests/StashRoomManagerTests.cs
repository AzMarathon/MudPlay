using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// StashRoomManager on-entry stash dispatch driven by user-marked rooms from
// CharacterProfile.StashRooms. The manager offloads every coin denomination at or
// below CashSettings.StashCoinCutoff (or all of them when it's Everything) into
// lowest-denomination-first `hide N <coin>` commands, so the coins left on hand
// are the fewest possible. The keep-on-hand floor is a BANKING rule (tested with
// AutoDepositManager), not a stash rule. Held amounts come from the authoritative
// InventorySnapshot (the `i`-seeded, delta-tracked holdings) — not a local
// pickup tally, which would undercount the starting balance.
public sealed class StashRoomManagerTests
{
    private sealed class Harness : IDisposable
    {
        public LogService Log { get; } = new();
        public ProfileService Profile { get; } = new();
        public StashRoomManager Stash { get; }
        public List<byte[]> Sent { get; } = new();
        public CashSettings CashSettings { get; set; } = new();
        public bool AutoGetCashEnabled { get; set; } = true;
        // Per-denomination holdings + carried items the stash plan reads.
        // Seed before ExecuteStash to model what an `i` parse would have
        // produced.
        public InventorySnapshot Snapshot { get; set; } = InventorySnapshot.Empty;
        // Carried entries whose resolver returns the same name (i.e. the
        // item is flagged AutoStash). Anything not here resolves to null.
        public HashSet<string> AutoStashItems { get; } = new();
        public List<StashRoomManager.StashDispatch> Executed { get; } = new();
        public bool Paradigm { get; set; }

        public Harness()
        {
            Profile.LoadBlank();
            Stash = new StashRoomManager(Profile,
                readCash: () => CashSettings,
                getSnapshot: () => Snapshot,
                resolveAutoStashItem: entry => AutoStashItems.Contains(entry) ? entry : null,
                isEnabled: () => AutoGetCashEnabled,
                log: Log,
                isParadigm: () => Paradigm);
            Stash.SetWireSender(b => Sent.Add(b));
            Stash.StashExecuted += d => Executed.Add(d);
        }

        public void MarkRoomAsStash(int map, int room)
        {
            CharacterProfile p = Profile.Current!;
            p.StashRooms ??= new List<RoomRef>();
            p.StashRooms.Add(new RoomRef(map, room));
        }

        public IEnumerable<string> SentLines() =>
            Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        public void Dispose() => Stash.Dispose();
    }

    // Holdings snapshot with the given per-denomination coin counts and optional
    // carried items. TotalCopperValue is deliberately 0 — the stash plan sums
    // wealth from the per-coin counts, so a bogus consolidated value must not
    // leak into the offload math.
    private static InventorySnapshot Coins(
        int copper = 0, int silver = 0, int gold = 0, int platinum = 0, int runic = 0,
        params string[] carried)
    {
        return new InventorySnapshot(
            new CurrencyHoldings(copper, silver, gold, platinum, runic, 0),
            EncumbranceReading.Empty,
            Array.Empty<EquippedItem>(),
            carried,
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Enter_MatchingRoom_DumpsAll_WhenNoKeep()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(gold: 500);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Single(h.Sent);
        Assert.Equal("hide 500 gold crown", h.SentLines().First());
    }

    [Fact]
    public void Enter_OnlyStashUpTo_KeepsHigherDenominations()
    {
        // "Only stash up to Gold" hides copper / silver / gold and keeps the higher
        // platinum / runic in hand.
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.StashCoinCutoff = StashCoinCutoff.Gold;
        h.Snapshot = Coins(silver: 30, gold: 40, platinum: 5, runic: 2);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        List<string> lines = h.SentLines().ToList();
        Assert.Equal(2, lines.Count);   // platinum + runic left in hand
        Assert.Equal("hide 30 silver noble", lines[0]);
        Assert.Equal("hide 40 gold crown", lines[1]);
    }

    [Fact]
    public void Enter_OnlyStashUpTo_AllHeldAboveFilter_NoDispatch()
    {
        // Holding only platinum with "up to Gold" ⇒ nothing eligible, no coin hide.
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.StashCoinCutoff = StashCoinCutoff.Gold;
        h.Snapshot = Coins(platinum: 5);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Enter_NonMatchingRoom_NoDispatch()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(gold: 500);

        h.Stash.ExecuteStash(new RoomKey(2, 99));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Enter_MultipleCurrencies_DispatchesEach()
    {
        // No coin filter ⇒ every held denomination is offloaded whole, one
        // `hide N <coin>` apiece, lowest denomination first.
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(silver: 50, gold: 250, platinum: 12);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Equal(3, h.Sent.Count);
        List<string> lines = h.SentLines().ToList();
        Assert.Equal("hide 50 silver noble", lines[0]);
        Assert.Equal("hide 250 gold crown", lines[1]);
        Assert.Equal("hide 12 platinum piece", lines[2]);
        Assert.Equal(3, h.Executed[0].Currencies.Count);
    }

    [Fact]
    public void AllDenominations_NoKeep_HidesEach()
    {
        // With no keep floor every held denomination is offloaded whole, one
        // `hide N <coin>` apiece, lowest denomination first.
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(copper: 7, silver: 6, gold: 5, platinum: 4, runic: 3);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        List<string> lines = h.SentLines().ToList();
        Assert.Equal(5, lines.Count);
        Assert.Equal("hide 7 copper farthing", lines[0]);
        Assert.Equal("hide 6 silver noble", lines[1]);
        Assert.Equal("hide 5 gold crown", lines[2]);
        Assert.Equal("hide 4 platinum piece", lines[3]);
        Assert.Equal("hide 3 runic coin", lines[4]);
    }

    [Fact]
    public void OnlyStashUpTo_Platinum_KeepsRunic()
    {
        // "Up to Platinum" stashes copper → platinum whole and keeps runic in hand.
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.StashCoinCutoff = StashCoinCutoff.Platinum;
        h.Snapshot = Coins(copper: 8_000, silver: 900, gold: 40, platinum: 3, runic: 2);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        List<string> lines = h.SentLines().ToList();
        Assert.Equal(4, lines.Count);   // runic kept
        Assert.Equal("hide 8000 copper farthing", lines[0]);
        Assert.Equal("hide 900 silver noble", lines[1]);
        Assert.Equal("hide 40 gold crown", lines[2]);
        Assert.Equal("hide 3 platinum piece", lines[3]);
    }

    [Fact]
    public void AutoGetCashOff_NoDispatch()
    {
        using Harness h = new() { AutoGetCashEnabled = false };
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(gold: 100);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void NoStashRoomsConfigured_NoDispatch()
    {
        using Harness h = new();
        h.Snapshot = Coins(gold: 100);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void SecondVisit_AfterHoldingsStashed_NoReDispatch()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(gold: 500);

        h.Stash.ExecuteStash(new RoomKey(1, 42));
        Assert.Single(h.Sent);

        // After the server confirms the hide, the InventoryManager snapshot drops
        // to empty — a re-entry finds no coin to stash and stays quiet.
        h.Snapshot = Coins();
        h.Stash.ExecuteStash(new RoomKey(1, 42));
        Assert.Single(h.Sent);
    }

    // ===== item stashing (stash rooms hold cash AND items) =====

    [Fact]
    public void Enter_MatchingRoom_HidesFlaggedItem()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch" });
        h.AutoStashItems.Add("a torch");

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Equal("hide a torch", Assert.Single(h.SentLines()));
        Assert.Equal(new[] { "a torch" }, h.Executed[0].Items);
    }

    [Fact]
    public void Enter_UnflaggedItem_Stays()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch" });
        // Not flagged AutoStash — resolver returns null, nothing hidden.

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Enter_CashAndItems_BothDispatched()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(gold: 500, carried: new[] { "a torch", "a rusty dagger" });
        h.AutoStashItems.Add("a torch");
        h.AutoStashItems.Add("a rusty dagger");

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        List<string> lines = h.SentLines().ToList();
        Assert.Contains("hide 500 gold crown", lines);
        Assert.Contains("hide a torch", lines);
        Assert.Contains("hide a rusty dagger", lines);
        Assert.Single(h.Executed[0].Currencies);
        Assert.Equal(2, h.Executed[0].Items.Count);
    }

    [Fact]
    public void Enter_DuplicateFlaggedItem_HidesEach()
    {
        // MajorMUD lists each carried copy as its own token, so a stack of
        // three flagged torches yields three hides.
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch", "a torch", "a torch" });
        h.AutoStashItems.Add("a torch");

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Equal(3, h.Sent.Count);
        Assert.Equal(3, h.Executed[0].Items.Count);
    }

    [Fact]
    public void Paradigm_BatchesDuplicateHidesIntoOneCountedCommand()
    {
        using Harness h = new() { Paradigm = true };
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch", "a torch", "a torch" });
        h.AutoStashItems.Add("a torch");

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Equal("hide 3 a torch", Assert.Single(h.SentLines()));
        Assert.Equal(3, h.Executed[0].Items.Count);   // dispatched count preserved
    }

    [Fact]
    public void ItemsOnly_NoExcessCash_StillFiresEvent()
    {
        // No coin above keep, but a flagged item present — the event
        // fires so transaction history records the item stash.
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch" });
        h.AutoStashItems.Add("a torch");

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Single(h.Executed);
        Assert.Empty(h.Executed[0].Currencies);
        Assert.Equal(new[] { "a torch" }, h.Executed[0].Items);
    }

    [Fact]
    public void AutoGetCashOff_NoItemDispatch()
    {
        using Harness h = new() { AutoGetCashEnabled = false };
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch" });
        h.AutoStashItems.Add("a torch");

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Enter_NonMatchingRoom_NoItemDispatch()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch" });
        h.AutoStashItems.Add("a torch");

        h.Stash.ExecuteStash(new RoomKey(2, 99));

        Assert.Empty(h.Sent);
    }
}
