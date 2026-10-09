using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Inventory;
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

        private AutoDiscardManager.ResolvedDiscard? Resolve(string entry)
            => _map.TryGetValue(entry.Trim(), out (int Number, bool Discard, int Keep) v)
                ? new AutoDiscardManager.ResolvedDiscard(v.Number, entry.Trim(), v.Discard, v.Keep)
                : null;

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

    [Fact]
    public void EmitDiscard_DropMode_SendsDropPerCopy()
    {
        using Harness h = new();   // HideMode defaults off
        h.Map("moonstone", 7, discard: false);
        List<string> sent = new();

        string verb = h.Discard.EmitDiscard(sent.Add, "moonstone", 2);

        Assert.Equal("drop", verb);
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
        using Harness h = new();
        h.Enabled = false;
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: false);
        List<string> sent = new();

        string verb = h.Discard.EmitDiscard(sent.Add, "moonstone", 2);

        Assert.Equal("hide", verb);
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
        List<string> sent = new();

        h.Discard.EmitDiscard(sent.Add, "moonstone", 3);

        Assert.Equal(new[] { "hide 3 moonstone" }, sent);
    }

    [Fact]
    public void EmitDiscard_HideMode_KeepsItsHidesOutOfTheStashLedger()
    {
        using Harness h = new();
        h.Paradigm = true;
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: false);

        h.Discard.EmitDiscard(_ => { }, "moonstone", 3);

        // Paradigm confirms the pile in one counted line; it claims all three.
        Assert.True(h.Discard.TryConsumeSuppressedHide("3 moonstone"));
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
    }

    [Fact]
    public void EmitDiscard_NoName_SendsNothing()
    {
        // A bare `hide` would hide the character.
        using Harness h = new();
        h.Discard.HideMode = true;
        List<string> sent = new();

        h.Discard.EmitDiscard(sent.Add, " ", 1);

        Assert.Empty(sent);
    }

    [Fact]
    public void RefusedHide_ReleasesItsLedgerClaim_AndIsAnnounced()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: false);
        List<string> refused = new();
        h.Discard.HideRefused += refused.Add;

        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        Assert.Equal(new[] { "moonstone" }, refused);
        // The hide never happened, so a later stash of the item still records.
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
    }

    [Fact]
    public void RefusedDrop_IsNotAHideRefusal()
    {
        using Harness h = new();
        List<string> refused = new();
        h.Discard.HideRefused += refused.Add;

        h.Feed("There is no room to drop moonstone here.");

        Assert.Empty(refused);
    }
}
