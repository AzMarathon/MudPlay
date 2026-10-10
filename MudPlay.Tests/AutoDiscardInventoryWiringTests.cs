using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The real InventoryManager and the real AutoDiscardManager wired as AppServices
// wires them, so a confirmation line re-enters the evaluation through
// Inventory.Changed with the pack already changed under it. AutoDiscardManagerTests
// drives the engine by hand and can't show that an answer never sets off a second
// discard for the same copy.
public sealed class AutoDiscardInventoryWiringTests
{
    private sealed class Rig : IDisposable
    {
        private readonly MessageRouter _router = new();
        private readonly LineExtractor _lines = new(new TerminalEmulator(80, 24));
        private readonly Dictionary<string, (int Number, bool Discard, int Keep)> _map =
            new(StringComparer.OrdinalIgnoreCase);

        public InventoryManager Inventory { get; } = new(log: null, itemWeightResolver: null, slotResolver: null);
        public AutoDiscardManager Discard { get; }
        public List<string> Sent { get; } = new();

        // The live client dispatches a line to the router before the inventory
        // reads it. Both orders are run: neither may over-send.
        public Rig(bool routerFirst, bool paradigm = false)
        {
            DefaultPatterns.Seed(_router);
            if (routerFirst) _lines.LineEmitted += l => _router.Dispatch(l);
            Inventory.AttachLineExtractor(_lines);
            if (!routerFirst) _lines.LineEmitted += l => _router.Dispatch(l);

            Discard = new AutoDiscardManager(_router,
                carriedItems: () => Inventory.Snapshot.CarriedItems,
                resolve: Resolve,
                isEnabled: () => true,
                isParadigm: () => paradigm,
                wornItems: () =>
                {
                    InventorySnapshot snap = Inventory.Snapshot;
                    IEnumerable<string> worn = snap.EquippedItems.Select(e => e.Name);
                    return snap.ReadiedLight is { } lit ? worn.Append(lit.Name) : worn;
                });
            Discard.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
            Inventory.Changed += Discard.OnInventoryChanged;
            Inventory.FullInventoryParsed += Discard.OnFullInventoryRead;
            Inventory.ItemHidden += item => Discard.TryConsumeSuppressedHide(item);
            Inventory.ItemSold += (name, count, _) => Discard.ReleaseHeld(name, count);
        }

        public void Map(string name, int number, bool discard, int keep = 0) => _map[name] = (number, discard, keep);

        private AutoDiscardManager.ResolvedDiscard? Resolve(string entry)
        {
            string name = CountedCommand.SplitLeadingCount(entry.Trim()).Name;
            return _map.TryGetValue(name, out (int Number, bool Discard, int Keep) v)
                ? new AutoDiscardManager.ResolvedDiscard(v.Number, name, v.Discard, v.Keep)
                : null;
        }

        public void Feed(string text)
        {
            FieldInfo? field = typeof(LineExtractor).GetField(
                "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(_lines) is Action<LineExtractor.EmittedLine> handler)
                handler(new LineExtractor.EmittedLine(
                    text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public void FullInventory(string items)
        {
            Feed($"You are carrying {items}, 5 copper farthings.");
            Feed("You have no keys.");
            Feed("Wealth:    5 copper farthings");
            Feed("Encumbrance:    36/2880  -  Light  [1%]");
        }

        public int Carried(string name)
        {
            foreach (string entry in Inventory.Snapshot.CarriedItems)
            {
                (int count, string item) = CountedCommand.SplitLeadingCount(entry);
                if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase)) return count;
            }
            return 0;
        }

        public void Dispose()
        {
            Discard.Dispose();
            Inventory.Dispose();
        }
    }

    // A pile of ten with two to keep: eight go out at once, and as the answers
    // come back one at a time, each changing the pack under the engine, the total
    // stays eight, whichever of the two listeners hears the answer first.
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void PileWithAKeepAmount_GoesOutOnce_AndTheAnswersSendNothingMore(bool routerFirst, bool paradigm, bool hide)
    {
        using Rig r = new(routerFirst, paradigm);
        r.Discard.HideMode = hide;
        r.Map("dagger", 1, discard: true, keep: 2);
        string verb = hide ? "hide" : "drop";
        string past = hide ? "hid" : "dropped";

        r.FullInventory("10 dagger");

        if (paradigm)
        {
            Assert.Equal(new[] { $"{verb} 8 dagger" }, r.Sent);
            r.Feed($"You {past} 8 dagger.");
        }
        else
        {
            Assert.Equal(8, r.Sent.Count);
            Assert.All(r.Sent, s => Assert.Equal($"{verb} dagger", s));
            for (int answered = 1; answered <= 8; answered++)
            {
                r.Feed($"You {past} dagger.");
                Assert.Equal(10 - answered, r.Carried("dagger"));
                Assert.Equal(8, r.Sent.Count);
            }
        }

        int sent = paradigm ? 1 : 8;
        Assert.Equal(sent, r.Sent.Count);
        Assert.Equal(2, r.Carried("dagger"));
        Assert.Equal(0, r.Discard.UnansweredDrops + r.Discard.UnansweredHides);

        // The two kept stay through a later change of the pack and a later read.
        r.Feed("You picked up 3 copper farthings");
        r.FullInventory("2 dagger");
        Assert.Equal(sent, r.Sent.Count);
    }

    // The keep amount counts what is worn, read off the real `i` listing, and the
    // worn copy is never the one sent for.
    [Theory]
    [InlineData("gold ring (Finger), gold ring", 1, 1)]
    [InlineData("gold ring (Finger), 2 gold ring", 1, 2)]
    [InlineData("gold ring (Finger), 2 gold ring", 2, 1)]
    [InlineData("gold ring (Finger)", 0, 0)]
    [InlineData("2 gold ring", 1, 1)]
    public void WornCopiesInTheListing_CountTowardTheKeepAmount(string carrying, int keep, int discarded)
    {
        using Rig r = new(routerFirst: true);
        r.Map("gold ring", 5, discard: true, keep: keep);

        r.FullInventory(carrying);

        Assert.Equal(discarded, r.Sent.Count);
        Assert.All(r.Sent, s => Assert.Equal("drop gold ring", s));
    }

    // The lit light is a held copy too: with one torch to keep and the lit one
    // covering it, the spare goes, and no discard is ever sent for the lit one.
    [Fact]
    public void LitLightInTheListing_CountsTowardTheKeepAmount()
    {
        using Rig r = new(routerFirst: true);
        r.Map("torch", 3, discard: true, keep: 1);

        r.FullInventory("torch (Readied/200), torch");
        Assert.Equal(new[] { "drop torch" }, r.Sent);

        r.Feed("You dropped torch.");
        r.FullInventory("torch (Readied/190)");
        Assert.Single(r.Sent);
    }

    // Chest Offload's Drop on Stock in hide mode: one hide per copy of the pile, each
    // answer taking a copy out of the pack, and no second hide for any of them.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ByHandHidePile_OnStock_EachCopyAnsweredOnce(bool routerFirst)
    {
        using Rig r = new(routerFirst);
        r.Discard.HideMode = true;
        r.Map("moonstone", 7, discard: false);
        r.FullInventory("3 moonstone");
        List<string> byHand = new();

        Assert.Equal(3, r.Discard.EmitDiscard(byHand.Add, "moonstone", 3).Sent);
        Assert.Equal(0, r.Discard.EmitDiscard(byHand.Add, "moonstone", 3).Sent);

        r.Feed("You hid moonstone.");
        r.Feed("You hid moonstone.");
        r.Feed("There is no room to hide moonstone here.");

        Assert.Equal(3, byHand.Count);
        Assert.Empty(r.Sent);
        Assert.Equal(1, r.Carried("moonstone"));
        Assert.Equal(0, r.Discard.UnansweredHides);
        Assert.Equal(1, r.Discard.HeldFor("moonstone"));
    }

    // A full read files the lit light apart from the pack, so when the one copy is
    // the light in use there is nothing carried to discard, and nothing is sent. (On
    // Stock `drop torch` would take the lit torch as readily as a spare.)
    [Fact]
    public void ByHandDiscard_OfTheLitLight_WhenItIsTheOnlyCopy_SendsNothing()
    {
        using Rig r = new(routerFirst: true);
        r.Map("torch", 3, discard: false);
        r.FullInventory("torch (Readied/200)");
        List<string> byHand = new();

        Assert.Equal("torch", r.Inventory.Snapshot.ReadiedLight?.Name);
        Assert.Equal(0, r.Discard.EmitDiscard(byHand.Add, "torch", 1).Sent);
        Assert.Empty(byHand);
    }

    // After a reset the pack is the old one until it is read again. A coin picked up
    // first raises Changed and must not set the engine off; the read itself does.
    [Fact]
    public void AfterAReset_ACoinPickupSendsNothing_AndTheReadEvaluates()
    {
        using Rig r = new(routerFirst: true);
        r.Map("dagger", 1, discard: true);
        r.FullInventory("dagger");
        Assert.Single(r.Sent);

        r.Discard.Reset("disconnected", keepHeld: true);
        r.Feed("You picked up 3 copper farthings");
        Assert.Single(r.Sent);

        r.FullInventory("dagger");                // the drop was lost with the link
        Assert.Equal(2, r.Sent.Count);
    }
}
