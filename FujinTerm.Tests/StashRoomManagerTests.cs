using System.Text;
using FujinTerm.Game.Cash;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// StashRoomManager on-entry stash dispatch driven by user-marked rooms from
// CharacterProfile.StashRooms + the single raw KeepOnHandWealth floor on
// CashSettings. The manager decomposes coin held above that copper-farthing
// floor into lowest-denomination-first `hide N <coin>` commands, so the coins
// left on hand are the fewest possible. Held amounts come from the authoritative
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

        public Harness()
        {
            Profile.LoadBlank();
            Stash = new StashRoomManager(Profile,
                readCash: () => CashSettings,
                getSnapshot: () => Snapshot,
                resolveAutoStashItem: entry => AutoStashItems.Contains(entry) ? entry : null,
                isEnabled: () => AutoGetCashEnabled,
                log: Log);
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
        Assert.Equal("hide 500 gold", h.SentLines().First());
    }

    [Fact]
    public void Enter_MatchingRoom_KeepsConfiguredAmount()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepOnHandWealth = 10_000; // 100 gold worth of copper
        h.Snapshot = Coins(gold: 500);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Single(h.Sent);
        Assert.Equal("hide 400 gold", h.SentLines().First());
    }

    [Fact]
    public void Enter_HeldAtOrBelowKeep_NoDispatch()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepOnHandWealth = 10_000; // 100 gold worth of copper
        h.Snapshot = Coins(gold: 80);

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
        // Held = 50 silver (500) + 250 gold (25,000) + 12 platinum (120,000) =
        // 145,500 copper. Keep 100,000 → 45,500 excess. Greedy lowest-first sheds
        // the cheap coins to leave the fewest on hand: 50 silver (500) leaves
        // 45,000 → 250 gold (25,000) leaves 20,000 → 2 platinum (20,000) leaves 0.
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepOnHandWealth = 100_000;
        h.Snapshot = Coins(silver: 50, gold: 250, platinum: 12);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Equal(3, h.Sent.Count);
        List<string> lines = h.SentLines().ToList();
        Assert.Equal("hide 50 silver", lines[0]);
        Assert.Equal("hide 250 gold", lines[1]);
        Assert.Equal("hide 2 platinum", lines[2]);
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
        Assert.Equal("hide 7 copper", lines[0]);
        Assert.Equal("hide 6 silver", lines[1]);
        Assert.Equal("hide 5 gold", lines[2]);
        Assert.Equal("hide 4 platinum", lines[3]);
        Assert.Equal("hide 3 runic", lines[4]);
    }

    [Fact]
    public void RawKeep_MixedCurrency_KeepsFloor()
    {
        // The reported scenario: keep 100,000 copper while holding mixed coin.
        // Held = 3 platinum (30,000) + 40 gold (4,000) + 900 silver (9,000) +
        // 8,000 copper = 51,000 — all below the floor, so nothing is stashed.
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepOnHandWealth = 100_000;
        h.Snapshot = Coins(copper: 8_000, silver: 900, gold: 40, platinum: 3);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Empty(h.Sent);

        // Now add platinum to clear the floor: held = 120,000 + 4,000 + 9,000 +
        // 8,000 = 141,000. Keep 100,000 → 41,000 excess. Lowest-first sheds the
        // cheap coins whole: 8,000 copper (8,000) leaves 33,000 → 900 silver
        // (9,000) leaves 24,000 → 40 gold (4,000) leaves 20,000 → 2 platinum
        // (20,000) leaves 0, keeping the remaining platinum on hand.
        h.Snapshot = Coins(copper: 8_000, silver: 900, gold: 40, platinum: 12);
        h.Stash.ExecuteStash(new RoomKey(1, 42));

        List<string> lines = h.SentLines().ToList();
        Assert.Equal(4, lines.Count);
        Assert.Equal("hide 8000 copper", lines[0]);
        Assert.Equal("hide 900 silver", lines[1]);
        Assert.Equal("hide 40 gold", lines[2]);
        Assert.Equal("hide 2 platinum", lines[3]);
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
    public void SecondVisit_AfterHoldingsDrop_NoReDispatch()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepOnHandWealth = 10_000; // 100 gold worth of copper
        h.Snapshot = Coins(gold: 500);

        h.Stash.ExecuteStash(new RoomKey(1, 42));
        Assert.Single(h.Sent);

        // After the server confirms the hide, the InventoryManager
        // snapshot drops to the kept floor — a re-entry finds nothing
        // above keep and stays quiet.
        h.Snapshot = Coins(gold: 100);
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
        h.CashSettings.KeepOnHandWealth = 10_000; // 100 gold worth of copper
        h.Snapshot = Coins(gold: 500, carried: new[] { "a torch", "a rusty dagger" });
        h.AutoStashItems.Add("a torch");
        h.AutoStashItems.Add("a rusty dagger");

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        List<string> lines = h.SentLines().ToList();
        Assert.Contains("hide 400 gold", lines);
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
