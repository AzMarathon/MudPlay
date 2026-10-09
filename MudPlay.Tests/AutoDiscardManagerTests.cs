using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// AutoDiscardManager: on any inventory change, drops each carried item flagged
// AutoDiscard down to its keep floor — one `drop` per copy. Drops it has sent but
// not yet seen confirmed are held in-flight and subtracted from the live count so
// the Changed events its own drops raise don't re-send; a self "You dropped ..."
// clears the in-flight count, another player's drop is ignored.
public sealed class AutoDiscardManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public AutoDiscardManager Discard { get; }
        public List<byte[]> Sent { get; } = new();
        public List<string> Carried { get; } = new();
        public bool Enabled { get; set; } = true;
        public bool Paradigm { get; set; }

        // name -> (Number, Discard, Keep)
        private readonly Dictionary<string, (int Number, bool Discard, int Keep)> _map =
            new(StringComparer.OrdinalIgnoreCase);

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Discard = new AutoDiscardManager(Router,
                carriedItems: () => Carried,
                resolve: Resolve,
                isEnabled: () => Enabled,
                log: Log,
                isParadigm: () => Paradigm);
            Discard.SetWireSender(b => Sent.Add(b));
        }

        public void Map(string name, int number, bool discard, int keep = 0)
            => _map[name] = (number, discard, keep);

        // Like the live resolver, reads past a stack's leading count ("3 moonstone").
        private AutoDiscardManager.ResolvedDiscard? Resolve(string entry)
        {
            string name = CountedCommand.SplitLeadingCount(entry.Trim()).Name;
            return _map.TryGetValue(name, out (int Number, bool Discard, int Keep) v)
                ? new AutoDiscardManager.ResolvedDiscard(v.Number, name, v.Discard, v.Keep)
                : null;
        }

        // The game's "You hid …" as both listeners see it: the router line, and the
        // transaction-history forwarder asking whether the hide was a discard.
        public bool Hid(string token)
        {
            Feed($"You hid {token}.");
            return Discard.TryConsumeSuppressedHide(token);
        }

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public List<string> SentText => Sent
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public void Dispose() => Discard.Dispose();
    }

    [Fact]
    public void FlaggedItem_DropsEveryCopy()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.AddRange(new[] { "dagger", "dagger", "dagger" });

        h.Discard.OnInventoryChanged();

        Assert.Equal(3, h.SentText.Count);
        Assert.All(h.SentText, s => Assert.Equal("drop dagger", s));
    }

    [Fact]
    public void DuringRoombaSweep_NoDrop_EvenWhenFlagged()
    {
        // A Roomba sweep is sorting the house: auto-discard is held off so it can't
        // bin an item Roomba is relocating. Roomba sorts flagged items itself;
        // normal discard resumes when the sweep ends.
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.AddRange(new[] { "dagger", "dagger", "dagger" });
        h.Discard.SuppressDuringSweep = () => true;

        h.Discard.OnInventoryChanged();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Paradigm_BatchesDropIntoOneCountedCommand()
    {
        using Harness h = new() { Paradigm = true };
        h.Map("dagger", 1, discard: true);
        h.Carried.AddRange(new[] { "dagger", "dagger", "dagger" });

        h.Discard.OnInventoryChanged();

        Assert.Equal("drop 3 dagger", Assert.Single(h.SentText));
    }

    [Fact]
    public void Paradigm_CountedConfirmation_ClearsInFlight_NoResend()
    {
        using Harness h = new() { Paradigm = true };
        h.Map("dagger", 1, discard: true);
        h.Carried.AddRange(new[] { "dagger", "dagger", "dagger" });

        h.Discard.OnInventoryChanged();
        Assert.Single(h.SentText);                 // one batched "drop 3 dagger"

        // The batched confirmation clears all three in-flight at once, so the
        // Changed re-evaluation it triggers doesn't re-drop.
        h.Feed("You dropped 3 dagger.");
        h.Carried.Clear();                          // dropped items gone from the pack
        h.Discard.OnInventoryChanged();

        Assert.Single(h.SentText);                 // still just the one command
    }

    [Fact]
    public void KeepFloor_LeavesMinimum()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true, keep: 1);
        h.Carried.AddRange(new[] { "dagger", "dagger", "dagger" });

        h.Discard.OnInventoryChanged();

        Assert.Equal(2, h.SentText.Count);   // 3 carried − 1 keep
    }

    [Fact]
    public void UnflaggedItem_NoDrop()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: false);
        h.Carried.Add("dagger");

        h.Discard.OnInventoryChanged();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void DisabledMaster_NoDrop()
    {
        using Harness h = new() { Enabled = false };
        h.Map("dagger", 1, discard: true);
        h.Carried.Add("dagger");

        h.Discard.OnInventoryChanged();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void InFlight_SuppressesResendUntilConfirmed()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.AddRange(new[] { "dagger", "dagger", "dagger" });

        h.Discard.OnInventoryChanged();      // sends 3 drops
        Assert.Equal(3, h.SentText.Count);

        // The three drops each raise a Changed while the server still reports the
        // copies carried — the in-flight guard must not re-send them.
        h.Discard.OnInventoryChanged();
        Assert.Equal(3, h.SentText.Count);
    }

    [Fact]
    public void SelfConfirmations_ClearInFlight_AllowFreshDrops()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.AddRange(new[] { "dagger", "dagger", "dagger" });

        h.Discard.OnInventoryChanged();      // 3 drops, in-flight = 3
        h.Feed("You dropped dagger.");
        h.Feed("You dropped dagger.");
        h.Feed("You dropped dagger.");       // in-flight cleared

        // A fresh chest dump pours in two more; with in-flight clear they drop.
        h.Carried.Clear();
        h.Carried.AddRange(new[] { "dagger", "dagger" });
        h.Discard.OnInventoryChanged();

        Assert.Equal(5, h.SentText.Count);   // 3 + 2
    }

    [Fact]
    public void OtherPlayerDrop_DoesNotClearInFlight()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.Add("dagger");

        h.Discard.OnInventoryChanged();      // 1 drop, in-flight = 1
        Assert.Single(h.Sent);

        h.Feed("Bob drops dagger.");          // another player's drop — ignored

        // In-flight still 1, so the same carried copy is not re-dropped.
        h.Discard.OnInventoryChanged();
        Assert.Single(h.Sent);
    }

    [Fact]
    public void HideMode_OffloadsWithHideNotDrop()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.AddRange(new[] { "dagger", "dagger" });

        h.Discard.OnInventoryChanged();

        Assert.Equal(2, h.SentText.Count);
        Assert.All(h.SentText, s => Assert.Equal("hide dagger", s));
    }

    [Fact]
    public void HideMode_SelfHideConfirmation_ClearsInFlight()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.AddRange(new[] { "dagger", "dagger" });

        h.Discard.OnInventoryChanged();      // 2 hides, in-flight = 2
        h.Feed("You hid dagger.");
        h.Feed("You hid dagger.");           // in-flight cleared

        // A fresh copy pours in; with in-flight clear it hides.
        h.Carried.Clear();
        h.Carried.Add("dagger");
        h.Discard.OnInventoryChanged();

        Assert.Equal(3, h.SentText.Count);   // 2 + 1
    }

    [Fact]
    public void TryConsumeSuppressedHide_ClaimsEngineHideOncePerCopy()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.AddRange(new[] { "dagger", "dagger" });

        h.Discard.OnInventoryChanged();      // 2 engine hides registered

        // The transaction-log forwarder claims each engine hide once — those two
        // are discards, not stashes, so they're kept out of the ledger.
        Assert.True(h.Discard.TryConsumeSuppressedHide("dagger"));
        Assert.True(h.Discard.TryConsumeSuppressedHide("dagger"));
        // A third hide of the same item was never an engine offload — it logs.
        Assert.False(h.Discard.TryConsumeSuppressedHide("dagger"));
    }

    [Fact]
    public void TryConsumeSuppressedHide_ManualHide_NotClaimed()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);

        // No engine offload has run, so a manual / stash-room hide is never
        // registered — the forwarder still records it.
        Assert.False(h.Discard.TryConsumeSuppressedHide("dagger"));
    }

    [Fact]
    public void DropMode_DoesNotRegisterSuppressedHides()
    {
        using Harness h = new();   // HideMode defaults off
        h.Map("dagger", 1, discard: true);
        h.Carried.Add("dagger");

        h.Discard.OnInventoryChanged();      // sends `drop dagger`

        // Drop-mode offloads aren't hides, so nothing is suppressed from the log.
        Assert.False(h.Discard.TryConsumeSuppressedHide("dagger"));
    }

    // ----- discards sent by hand (Chest Offload's Drop buttons) ----------

    private static readonly RoomKey RoomA = new(1, 100);
    private static readonly RoomKey RoomB = new(1, 101);
    private static readonly RoomKey RoomC = new(1, 102);

    // Hide mode on, standing in RoomA, carrying `copies` moonstones that are not
    // flagged for auto-discard (chest loot never is).
    private static Harness HidingMoonstones(int copies)
    {
        Harness h = new();
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: false);
        for (int i = 0; i < copies; i++) h.Carried.Add("moonstone");
        h.Discard.OnRoomEntered(RoomA);
        return h;
    }

    [Fact]
    public void EmitDiscard_DropMode_SendsDropPerCopy()
    {
        using Harness h = new();   // HideMode defaults off
        h.Map("moonstone", 7, discard: false);
        h.Carried.AddRange(new[] { "moonstone", "moonstone" });
        List<string> sent = new();

        (string verb, int count) = h.Discard.EmitDiscard(sent.Add, "moonstone", 2);

        Assert.Equal(("drop", 2), (verb, count));
        Assert.Equal(new[] { "drop moonstone", "drop moonstone" }, sent);
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
    }

    [Fact]
    public void EmitDiscard_HideMode_SendsHide_ForAnItemNotFlaggedForDiscard()
    {
        // The reported miss: a Drop from Chest Offload went out as `drop` with
        // "Hide items when discarding" ticked. Chest loot isn't flagged for
        // auto-discard, and the engine's own switch can be off: neither is part of
        // the hide-or-drop choice.
        using Harness h = HidingMoonstones(2);
        h.Enabled = false;
        List<string> sent = new();

        (string verb, int count) = h.Discard.EmitDiscard(sent.Add, "moonstone", 2);

        Assert.Equal(("hide", 2), (verb, count));
        Assert.Equal(new[] { "hide moonstone", "hide moonstone" }, sent);
        Assert.Empty(h.Sent);   // nothing goes out on the engine's own sender
    }

    [Fact]
    public void EmitDiscard_HideMode_Paradigm_SendsOneCountedHide()
    {
        using Harness h = new();
        h.Paradigm = true;
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: false);
        h.Carried.Add("3 moonstone");   // an `i` lists the stack as one counted entry
        List<string> sent = new();

        h.Discard.EmitDiscard(sent.Add, "moonstone", 3);

        Assert.Equal(new[] { "hide 3 moonstone" }, sent);
        // Paradigm confirms the pile in one counted line; it claims all three.
        Assert.True(h.Hid("3 moonstone"));
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
    }

    [Fact]
    public void EmitDiscard_NoName_SendsNothing()
    {
        // A bare `hide` would hide the character.
        using Harness h = HidingMoonstones(1);
        List<string> sent = new();

        Assert.Equal(0, h.Discard.EmitDiscard(sent.Add, " ", 1).Sent);
        Assert.Empty(sent);
    }

    // Stock answers a hide for an item that isn't held with nothing at all, and the
    // line is then taken for speech. So a second Drop before the first is answered,
    // or a Drop for more copies than are carried, must not reach the wire.
    [Fact]
    public void EmitDiscard_SecondPressBeforeTheAnswer_SendsNothing()
    {
        using Harness h = HidingMoonstones(2);
        List<string> sent = new();

        Assert.Equal(2, h.Discard.EmitDiscard(sent.Add, "moonstone", 2).Sent);
        Assert.Equal(0, h.Discard.EmitDiscard(sent.Add, "moonstone", 2).Sent);

        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public void EmitDiscard_SendsOnlyForCopiesCarried()
    {
        using Harness h = HidingMoonstones(1);
        List<string> sent = new();

        Assert.Equal(1, h.Discard.EmitDiscard(sent.Add, "moonstone", 3).Sent);

        h.Hid("moonstone");
        h.Carried.Clear();
        Assert.Equal(0, h.Discard.EmitDiscard(sent.Add, "moonstone", 3).Sent);
        Assert.Equal(new[] { "hide moonstone" }, sent);
    }

    // A repeated drop only earns the game's private "You don't have X to drop!",
    // so a by-hand drop is not counted and a room that refused one doesn't block
    // the next press.
    [Fact]
    public void EmitDiscard_DropMode_ARepeatIsNotHeldBack()
    {
        using Harness h = new();
        h.Map("moonstone", 7, discard: false);
        h.Carried.Add("moonstone");
        List<string> sent = new();

        h.Discard.EmitDiscard(sent.Add, "moonstone", 1);
        h.Feed("There is no room to drop moonstone here.");
        h.Discard.EmitDiscard(sent.Add, "moonstone", 1);

        Assert.Equal(new[] { "drop moonstone", "drop moonstone" }, sent);
    }

    // ----- a hide the room has no room for ---------------------------------

    [Fact]
    public void StockPartial_TwoHidden_OneRefused_HoldsOne_NoLedgerResidue()
    {
        using Harness h = HidingMoonstones(3);
        List<string> refused = new();
        h.Discard.HideRefused += refused.Add;

        h.Discard.EmitDiscard(_ => { }, "moonstone", 3);
        Assert.True(h.Hid("moonstone"));
        Assert.True(h.Hid("moonstone"));
        h.Carried.RemoveRange(0, 2);
        h.Feed("There is no room to hide moonstone here.");

        Assert.Equal(new[] { "moonstone" }, refused);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));
        Assert.Equal(0, h.Discard.UnansweredHides);
        // The refused hide's ledger claim went with it: a stash of the item now
        // would still record.
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
        // The held copy is spoken for: another press in this room sends nothing.
        Assert.Equal(0, h.Discard.EmitDiscard(_ => { }, "moonstone", 1).Sent);
    }

    [Fact]
    public void RefusedHide_IsSentAgainOnceInTheNextRoom_AndSettlesWhenItLands()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");
        Assert.Empty(h.Sent);

        h.Discard.OnRoomEntered(RoomB);
        h.Discard.OnRoomEntered(RoomB);   // the room shown again

        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));   // not over until it lands
        Assert.Equal(1, h.Discard.UnansweredHides);

        // It lands: still a discard to the ledger, and nothing is left counted.
        List<string> ended = new();
        h.Discard.HeldHideEnded += ended.Add;
        Assert.True(h.Hid("moonstone"));
        h.Carried.Clear();
        Assert.Equal(0, h.Discard.UnansweredHides);
        Assert.Empty(h.Discard.HeldHides);
        Assert.Equal(new[] { "moonstone" }, ended);
        Assert.Contains(h.Log.Snapshot(), e => e.Message.Contains("held hide of moonstone landed"));
    }

    [Fact]
    public void HeldHide_RefusedAgain_WaitsForTheRoomAfter()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.OnRoomEntered(RoomB);
        h.Feed("There is no room to hide moonstone here.");
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));
        Assert.Single(h.Sent);            // one try per room

        h.Discard.OnRoomEntered(RoomC);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void HeldHide_IsNotRetriedOnARedisplayOfTheSameRoom()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.OnRoomEntered(RoomA);

        Assert.Empty(h.Sent);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));
    }

    [Fact]
    public void HeldHide_NoLongerCarried_IsForgotten()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Carried.Clear();   // sold, dropped or given away meanwhile
        h.Discard.OnRoomEntered(RoomB);

        Assert.Empty(h.Sent);
        Assert.Equal(0, h.Discard.HeldFor("moonstone"));
    }

    [Fact]
    public void RefusalOfAHideNeverRegistered_HoldsNothing_AndLeavesAnotherItemsClaimAlone()
    {
        using Harness h = HidingMoonstones(1);
        h.Map("ruby", 8, discard: false);
        h.Carried.Add("ruby");
        List<string> refused = new();
        h.Discard.HideRefused += refused.Add;
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);

        h.Feed("There is no room to hide ruby here.");   // a typed hide, or Hide All's

        Assert.Empty(refused);
        Assert.Equal(0, h.Discard.HeldFor("ruby"));
        Assert.Equal(1, h.Discard.UnansweredHides);
        Assert.True(h.Discard.TryConsumeSuppressedHide("moonstone"));
    }

    [Fact]
    public void HeldHide_WaitsOutARoombaSweep()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        bool sweeping = true;
        h.Discard.SuppressDuringSweep = () => sweeping;
        h.Discard.OnRoomEntered(RoomB);
        Assert.Empty(h.Sent);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));

        sweeping = false;
        h.Discard.OnRoomEntered(RoomC);
        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
    }

    [Fact]
    public void HeldHides_GoOutThroughThePacer_WhenOneIsBound()
    {
        using Harness h = HidingMoonstones(2);
        List<IReadOnlyList<string>> batches = new();
        h.Discard.PacedSender = batches.Add;
        h.Discard.EmitDiscard(_ => { }, "moonstone", 2);
        h.Feed("There is no room to hide moonstone here.");
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.OnRoomEntered(RoomB);

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { "hide moonstone", "hide moonstone" }, Assert.Single(batches));
    }

    [Fact]
    public void HideModeTurnedOff_GivesUpHeldHides()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.HideMode = false;
        h.Discard.OnRoomEntered(RoomB);

        Assert.Empty(h.Sent);
        Assert.Empty(h.Discard.HeldHides);
    }

    [Fact]
    public void EngineHide_Refused_WaitsForTheNextRoom()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Add("dagger");
        h.Discard.OnRoomEntered(RoomA);

        h.Discard.OnInventoryChanged();
        h.Feed("There is no room to hide dagger here.");
        h.Discard.OnInventoryChanged();   // this room has refused it already
        Assert.Single(h.Sent);

        h.Discard.OnRoomEntered(RoomB);
        Assert.Equal(new[] { "hide dagger", "hide dagger" }, h.SentText);

        // Its switch turned off while a copy waits: the engine's hide stays held,
        // and goes out once the switch is back.
        h.Feed("There is no room to hide dagger here.");
        h.Enabled = false;
        h.Discard.OnRoomEntered(RoomC);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal(1, h.Discard.HeldFor("dagger"));

        h.Enabled = true;
        h.Discard.RecheckHeldHides();
        Assert.Equal(3, h.Sent.Count);
    }

    [Fact]
    public void RetryLands_ThenNothingMoreIsSentInThatRoom()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Add("dagger");
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();
        h.Feed("There is no room to hide dagger here.");

        h.Discard.OnRoomEntered(RoomB);
        h.Hid("dagger");
        h.Carried.Clear();
        h.Discard.OnInventoryChanged();
        h.Discard.OnInventoryChanged();

        Assert.Equal(2, h.Sent.Count);   // the first try and the one retry
    }

    // ----- a held hide is for a hide still wanted ---------------------------

    // Two moonstones of the player's own and one from a chest: the chest's hide is
    // refused and held, then the chest's copy goes another way. A retry would hide
    // one of the player's own.
    private static Harness OwnTwoAndAHeldChestMoonstone()
    {
        Harness h = HidingMoonstones(3);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);   // the chest gave one
        h.Feed("There is no room to hide moonstone here.");
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));
        return h;
    }

    [Fact]
    public void HeldHide_IsCalledOff_WhenTheCopyIsSold()
    {
        using Harness h = OwnTwoAndAHeldChestMoonstone();
        List<string> ended = new();
        h.Discard.HeldHideEnded += ended.Add;

        h.Discard.ReleaseHeld("moonstone", 1);   // "You sold moonstone …", by the wiring
        h.Carried.RemoveAt(0);
        h.Discard.OnRoomEntered(RoomB);

        Assert.Empty(h.Sent);
        Assert.Empty(h.Discard.HeldHides);
        Assert.Equal(new[] { "moonstone" }, ended);
    }

    [Theory]
    [InlineData("You dropped moonstone.")]
    [InlineData("You hid moonstone.")]     // typed, or Hide All: not a hide sent here
    public void HeldHide_IsCalledOff_WhenTheCopyIsDroppedOrHiddenByHand(string line)
    {
        using Harness h = OwnTwoAndAHeldChestMoonstone();

        h.Feed(line);
        h.Carried.RemoveAt(0);
        h.Discard.OnRoomEntered(RoomB);

        Assert.Empty(h.Sent);
        Assert.Empty(h.Discard.HeldHides);
    }

    [Fact]
    public void ReleaseHeld_ByHandOnly_LeavesTheEnginesOwnHold()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Add("dagger");
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();
        h.Feed("There is no room to hide dagger here.");

        h.Discard.ReleaseHeld("dagger", int.MaxValue, byHandOnly: true);   // ✕ on its row

        Assert.Equal(1, h.Discard.HeldFor("dagger"));
    }

    // A by-hand discard never answered to the auto-discard switch, whether or not
    // the item happens to be flagged.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ByHandHeldHide_IsRetriedWithTheEngineOff(bool flagged)
    {
        using Harness h = new();
        h.Enabled = false;
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: flagged);
        h.Carried.Add("moonstone");
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.OnRoomEntered(RoomB);

        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
    }

    // ----- a hide that would get no answer ----------------------------------

    [Fact]
    public void HeldHide_WaitsWhileAHideCannotBeAnswered()
    {
        // The send gate is up, or the character is blind or in the dark: Stock
        // answers the hide with the can't-see line and hides nothing.
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        bool canHide = false;
        h.Discard.CanHideHere = () => canHide;
        h.Discard.OnRoomEntered(RoomB);
        Assert.Empty(h.Sent);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));

        // It clears while still in that room: the next inventory change looks again.
        canHide = true;
        h.Discard.OnInventoryChanged();
        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
    }

    [Fact]
    public void EnginesOwnHide_IsNotSentWhileItCannotBeAnswered()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Add("dagger");
        bool canHide = false;
        h.Discard.CanHideHere = () => canHide;

        h.Discard.OnInventoryChanged();
        Assert.Empty(h.Sent);
        Assert.Equal(0, h.Discard.UnansweredHides);

        canHide = true;
        h.Discard.OnInventoryChanged();
        Assert.Equal(new[] { "hide dagger" }, h.SentText);
    }

    [Fact]
    public void RetryNeverAnswered_IsHeldAgain_NotForgotten()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");
        h.Discard.OnRoomEntered(RoomB);          // the retry goes out and is lost
        Assert.Single(h.Sent);

        h.Discard.OnFullInventoryRead();
        h.Discard.OnFullInventoryRead();

        Assert.Equal(0, h.Discard.UnansweredHides);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));
        h.Discard.OnInventoryChanged();           // still room B, where it went out
        Assert.Single(h.Sent);
        h.Discard.OnRoomEntered(RoomC);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void SweepEnding_RechecksHeldHides_ButNotInTheRoomThatRefused()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.RecheckHeldHides();             // still room A
        Assert.Empty(h.Sent);

        bool sweeping = true;
        h.Discard.SuppressDuringSweep = () => sweeping;
        h.Discard.OnRoomEntered(RoomB);
        sweeping = false;
        h.Discard.RecheckHeldHides();             // the sweep ended in room B

        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
    }

    [Fact]
    public void Disconnect_KeepsHeldHides_TiedToTheRoomThatRefused()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Add("dagger");
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();
        h.Feed("There is no room to hide dagger here.");

        h.Discard.Reset("disconnected", keepHeld: true);
        h.Discard.OnRoomEntered(RoomA);           // back in, the same room
        h.Discard.OnInventoryChanged();
        Assert.Single(h.Sent);                    // not sent into the full room again

        h.Discard.OnRoomEntered(RoomB);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void HideModeOffThenOn_RestoresNothing_AndALaterHideIsNotALanding()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");
        h.Discard.OnRoomEntered(RoomB);           // retry in flight

        h.Discard.HideMode = false;
        h.Discard.HideMode = true;
        h.Hid("moonstone");

        Assert.Empty(h.Discard.HeldHides);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Message.Contains("landed"));
    }

    [Fact]
    public void RefusalArrivingWithHideModeOff_HoldsNothing()
    {
        using Harness h = HidingMoonstones(1);
        List<string> refused = new();
        h.Discard.HideRefused += refused.Add;
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);

        h.Discard.HideMode = false;
        h.Feed("There is no room to hide moonstone here.");

        Assert.Empty(refused);
        Assert.Empty(h.Discard.HeldHides);
    }

    // ----- nothing unanswered is counted forever ----------------------------

    [Fact]
    public void FullInventoryRead_ForgetsAHideForACopyNoLongerCarried()
    {
        using Harness h = HidingMoonstones(2);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 2);

        h.Carried.RemoveAt(0);   // the read shows one left
        h.Discard.OnFullInventoryRead();

        Assert.Equal(1, h.Discard.UnansweredHides);
        Assert.True(h.Discard.TryConsumeSuppressedHide("moonstone"));
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
    }

    [Fact]
    public void FullInventoryRead_CapsHeldHidesAtWhatIsCarried()
    {
        using Harness h = HidingMoonstones(2);
        List<string> ended = new();
        h.Discard.HeldHideEnded += ended.Add;
        h.Discard.EmitDiscard(_ => { }, "moonstone", 2);
        h.Feed("There is no room to hide moonstone here.");
        h.Feed("There is no room to hide moonstone here.");

        h.Carried.RemoveAt(0);
        h.Discard.OnFullInventoryRead();
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));

        h.Carried.Clear();
        h.Discard.OnFullInventoryRead();
        Assert.Empty(h.Discard.HeldHides);
        Assert.Equal(new[] { "moonstone" }, ended);
    }

    [Fact]
    public void ParadigmPartialEcho_LeavesTheRestCounted_UntilReadsGoQuiet()
    {
        using Harness h = new();
        h.Paradigm = true;
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: false);
        h.Carried.Add("3 moonstone");
        List<string> sent = new();
        h.Discard.EmitDiscard(sent.Add, "moonstone", 3);

        h.Hid("2 moonstone");
        h.Carried[0] = "moonstone";
        Assert.Equal(1, h.Discard.UnansweredHides);

        h.Discard.OnFullInventoryRead();
        Assert.Equal(1, h.Discard.UnansweredHides);
        h.Discard.OnFullInventoryRead();
        Assert.Equal(0, h.Discard.UnansweredHides);
        Assert.Equal(1, h.Discard.EmitDiscard(sent.Add, "moonstone", 1).Sent);
    }

    // Commands handed to the pacer are counted at once and sent a few at a time.
    // While some are still queued, quiet reads say nothing about the game.
    [Fact]
    public void QuietInventoryReads_ForgetNothingWhileThePacerStillHoldsCommands()
    {
        using Harness h = HidingMoonstones(3);
        bool queued = true;
        h.Discard.SendsQueued = () => queued;
        h.Discard.EmitDiscard(_ => { }, "moonstone", 3);

        h.Discard.OnFullInventoryRead();
        h.Discard.OnFullInventoryRead();
        h.Discard.OnFullInventoryRead();
        Assert.Equal(3, h.Discard.UnansweredHides);
        Assert.Equal(0, h.Discard.EmitDiscard(_ => { }, "moonstone", 3).Sent);

        queued = false;
        h.Discard.OnFullInventoryRead();   // the queue emptied since the last read
        Assert.Equal(3, h.Discard.UnansweredHides);
        h.Discard.OnFullInventoryRead();
        Assert.Equal(0, h.Discard.UnansweredHides);
    }

    [Fact]
    public void QuietInventoryReads_ForgetAHideTheGameNeverAnswered()
    {
        // `You may not hide that item!` names no item, a dark room answers with its
        // own line, a command sent too fast is dropped: none settles the count.
        using Harness h = HidingMoonstones(1);
        List<string> sent = new();
        h.Discard.EmitDiscard(sent.Add, "moonstone", 1);

        h.Discard.OnFullInventoryRead();   // may have been asked for before the hide
        Assert.Equal(0, h.Discard.EmitDiscard(sent.Add, "moonstone", 1).Sent);

        h.Discard.OnFullInventoryRead();   // nothing sent or answered since the last
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
        Assert.Equal(1, h.Discard.EmitDiscard(sent.Add, "moonstone", 1).Sent);
    }

    [Fact]
    public void InventoryReads_KeepCountingWhileAnswersStillArrive()
    {
        using Harness h = HidingMoonstones(3);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 3);

        h.Discard.OnFullInventoryRead();
        h.Hid("moonstone");                // a paced batch still draining
        h.Carried.RemoveAt(0);
        h.Discard.OnFullInventoryRead();

        Assert.Equal(2, h.Discard.UnansweredHides);
    }

    [Fact]
    public void Reset_ForgetsUnansweredAndHeldHides()
    {
        using Harness h = HidingMoonstones(2);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 2);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.Reset("death");

        Assert.Equal(0, h.Discard.UnansweredHides);
        Assert.Empty(h.Discard.HeldHides);
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
        h.Discard.OnRoomEntered(RoomB);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void RefusedDrop_IsNotAHideRefusal()
    {
        using Harness h = HidingMoonstones(1);
        List<string> refused = new();
        h.Discard.HideRefused += refused.Add;
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);

        h.Feed("There is no room to drop moonstone here.");

        Assert.Empty(refused);
        Assert.Equal(1, h.Discard.UnansweredHides);
    }
}
