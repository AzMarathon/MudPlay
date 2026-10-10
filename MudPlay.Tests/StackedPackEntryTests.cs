using System.Reflection;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The pack holds identical items as one entry under a leading count ("3 dagger"),
// whether they came off an 'i' read or one get at a time. These feed that live
// shape through the real InventoryManager into the engines that used to read an
// entry as one item named by its raw text.
public sealed class StackedPackEntryTests
{
    // A real InventoryManager on a line stream the test feeds.
    private sealed class LivePack : IDisposable
    {
        public InventoryManager Inv { get; } = new();
        private readonly LineExtractor _lines = new(new TerminalEmulator(80, 24));

        public LivePack() => Inv.AttachLineExtractor(_lines);

        public InventorySnapshot Snapshot => Inv.Snapshot;

        public void Feed(string text)
        {
            FieldInfo? field = typeof(LineExtractor).GetField(
                "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(_lines) is Action<LineExtractor.EmittedLine> handler)
                handler(new LineExtractor.EmittedLine(
                    text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        // A full 'i' read listing exactly these entries, as the game words them.
        public void Read(params string[] entries)
        {
            Feed($"You are carrying {string.Join(", ", entries)}");
            Feed("You have no keys.");
            Feed("Wealth: 0 copper farthings");
            Feed("Encumbrance: 10/2880 - None [0%]");
        }

        public void Dispose() => Inv.Dispose();
    }

    private static List<string> Text(IEnumerable<byte[]> sent)
        => sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

    // ===== the shared reading of the pack =====

    [Fact]
    public void APile_IsOneEntry_FromAReadAndFromGets()
    {
        using LivePack pack = new();
        pack.Read("3 dagger", "torch", "longsword (Weapon Hand)");
        pack.Feed("You took torch.");

        InventorySnapshot snap = pack.Snapshot;

        Assert.Equal(new[] { "3 dagger", "2 torch" }, snap.CarriedItems);
        Assert.Equal(3, snap.PackCount("dagger"));
        Assert.Equal(2, snap.PackCount("Torch"));
        Assert.Equal(0, snap.PackCount("longsword"));   // worn, not in the pack
        Assert.Equal(0, snap.PackCount("orch"));        // a whole name, not a fragment
        Assert.True(snap.PackNames().SetEquals(new[] { "dagger", "torch" }));
    }

    [Theory]
    [InlineData("silver dagger", true)]    // a pile in the pack
    [InlineData("torch", true)]            // a lone copy in the pack
    [InlineData("longsword", true)]        // worn
    [InlineData("dagger", false)]          // part of another item's name
    [InlineData("warhammer", false)]
    public void IsCarriedOrWorn_ReadsAPileByItsItemName(string name, bool held)
    {
        using LivePack pack = new();
        pack.Read("longsword (Weapon Hand)", "2 silver dagger", "torch");

        Assert.Equal(held, pack.Snapshot.IsCarriedOrWorn(name));
    }

    // ===== auto-open =====

    private sealed class OpenHarness : IDisposable
    {
        public LivePack Pack { get; } = new();
        public AutoOpenManager Open { get; }
        private readonly List<byte[]> _sent = new();

        // Item names flagged Auto-open, resolved as the item table does: by the
        // name with any count or article dropped.
        public OpenHarness(params string[] flagged)
        {
            Open = new AutoOpenManager(
                carriedItems: () => Pack.Snapshot.CarriedItems,
                resolve: entry =>
                {
                    int number = Array.FindIndex(flagged, f => f == ItemNameStore.Normalize(entry));
                    return number < 0 ? null : new AutoOpenManager.ResolvedOpen(number + 1, flagged[number], true);
                },
                isEnabled: () => true,
                isLoaded: () => Pack.Inv.IsLoaded,
                useTimer: false);
            Open.SetWireSender(_sent.Add);
            Pack.Inv.Changed += Open.OnInventoryChanged;
        }

        public List<string> Sent => Text(_sent);

        public void Dispose()
        {
            Open.Dispose();
            Pack.Dispose();
        }
    }

    [Fact]
    public void AutoOpen_SecondIdenticalContainer_JoiningAPile_IsOpened()
    {
        using OpenHarness h = new("small sack");
        h.Pack.Read("small sack");               // carried at connect: left shut

        h.Pack.Feed("You took small sack.");     // the pack now reads "2 small sack"

        Assert.Equal(new[] { "2 small sack" }, h.Pack.Snapshot.CarriedItems);
        Assert.Equal(new[] { "open small sack" }, h.Sent);
    }

    [Fact]
    public void AutoOpen_CountedPickup_OpensEachCopy()
    {
        using OpenHarness h = new("small sack");
        h.Pack.Read("torch");

        h.Pack.Feed("You took 2 small sack.");   // Paradigm's one line for two

        Assert.Equal(new[] { "open small sack", "open small sack" }, h.Sent);
    }

    [Fact]
    public void AutoOpen_LoneContainer_StillOpensOnce()
    {
        using OpenHarness h = new("small sack");
        h.Pack.Read("torch");

        h.Pack.Feed("You took small sack.");

        Assert.Equal(new[] { "open small sack" }, h.Sent);
    }

    [Fact]
    public void AutoOpen_APileThatStaysOrShrinks_IsNotOpenedAgain()
    {
        using OpenHarness h = new("small sack");
        h.Pack.Read("small sack");
        h.Pack.Feed("You took small sack.");
        h.Open.FlushInventoryForTests();
        Assert.Equal(new[] { "open small sack", "i" }, h.Sent);

        // The open failed and both are still there: no second try.
        h.Pack.Read("2 small sack");
        // Then it worked and one is gone.
        h.Pack.Read("small sack", "ruby");
        h.Open.FlushInventoryForTests();
        Assert.Equal(new[] { "open small sack", "i" }, h.Sent);

        // Another one picked up later is a new arrival.
        h.Pack.Feed("You took small sack.");
        Assert.Equal(new[] { "open small sack", "i", "open small sack" }, h.Sent);
    }

    // ===== gear: what a wear / eq can draw from =====

    private static EquipmentSet Set(EquipTriggerType trigger, params EquipmentSlotEntry[] slots)
        => new() { Trigger = trigger, Enabled = true, Name = trigger.ToString(), Slots = slots.ToList() };

    private static EquipmentManager Gear(LivePack pack, params EquipmentSet[] sets)
    {
        EquipmentSettings settings = new();
        settings.Sets.AddRange(sets);
        return new EquipmentManager(
            readEquipment: () => settings,
            getSnapshot: () => pack.Snapshot,
            readCombat: () => new CombatSettings(),
            writeCombat: _ => { },
            resolveItemSlot: name => name.Contains("helm", StringComparison.Ordinal) ? EquipmentSlot.Head : null);
    }

    [Theory]
    [InlineData("2 silver dagger")]
    [InlineData("silver dagger")]
    public void SwapWeapon_EquipsAWeaponHeldInThePack_AloneOrAsAPile(string entry)
    {
        using LivePack pack = new();
        pack.Read("longsword (Weapon Hand)", entry);
        EquipmentManager gear = Gear(pack);

        gear.SwapWeapon("silver dagger", null);

        Assert.Equal(new[] { "eq silver dagger" }, Text(gear.LastSentForTests));
    }

    [Fact]
    public void SwapWeapon_EquipsAnOffHandHeldAsAPile()
    {
        using LivePack pack = new();
        pack.Read("longsword (Weapon Hand)", "2 buckler");
        EquipmentManager gear = Gear(pack);

        gear.SwapWeapon("longsword", "buckler");

        Assert.Equal(new[] { "eq buckler" }, Text(gear.LastSentForTests));
    }

    [Fact]
    public void SwapWeapon_APileOfSomethingElse_IsNotTheWeapon()
    {
        using LivePack pack = new();
        pack.Read("longsword (Weapon Hand)", "2 silver dagger sheath");
        EquipmentManager gear = Gear(pack);

        gear.SwapWeapon("silver dagger", null);

        Assert.Empty(gear.LastSentForTests);
    }

    [Theory]
    [InlineData("2 padded helm")]
    [InlineData("padded helm")]
    public void AutoFiredSet_WearsAPieceHeldInThePack_AloneOrAsAPile(string entry)
    {
        using LivePack pack = new();
        pack.Read(entry, "torch");
        EquipmentSet resting = Set(EquipTriggerType.PreRestHp,
            new EquipmentSlotEntry(EquipmentSlot.Head, "padded helm"),
            new EquipmentSlotEntry(EquipmentSlot.Torso, "padded vest"));   // not held: left out
        EquipmentManager gear = Gear(pack, resting);

        Assert.Equal(EquipResult.Applied, gear.ApplyBySetId(resting.Id));
        Assert.Equal(new[] { "wear padded helm" }, Text(gear.LastSentForTests));
    }

    [Fact]
    public void BackstabArmor_WearsAPieceHeldAsAPile()
    {
        using LivePack pack = new();
        pack.Read("2 padded helm");
        EquipmentManager gear = Gear(pack,
            Set(EquipTriggerType.Backstab, new EquipmentSlotEntry(EquipmentSlot.Head, "padded helm")));

        Assert.Equal(EquipResult.Applied, gear.ApplyBackstabArmor());
        Assert.Equal(new[] { "wear padded helm" }, Text(gear.LastSentForTests));
    }

    [Fact]
    public void LocationItem_HeldAsAPile_IsWorn()
    {
        using LivePack pack = new();
        pack.Read("2 padded helm");
        EquipmentManager gear = Gear(pack);

        Assert.True(gear.SetSlotOverride("padded helm"));
        Assert.Equal(new[] { "wear padded helm" }, Text(gear.LastSentForTests));
    }
}
