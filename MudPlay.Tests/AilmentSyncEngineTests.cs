using System.Text;
using MudPlay.Game;
using MudPlay.Game.Conditions;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Outbound ailment-sync (AilmentSyncEngine): on a local VERBOSE ailment
// (blind / confuse / disease / held), announce a BARE '.@blind' on say (MegaMUD
// parity — no 'on'/'off' suffix) so other clients mirror our state; the four
// curable ailments also @wait the leader. On CLEAR the engine says NOTHING (no
// 'off' token) — the receiver clears the chip via a witnessed cure / spell-data
// duration / par P drop / @status reconcile — it only @ok's the leader to release
// the wait. POISON is NOT announced (an observer reads it from the par P flag) but
// still telepaths its @wait. The say only fires when in a party AND no cure spell
// is configured for that ailment; DoNotAnnounce* further gates the say, Ignore*
// gates the @wait — independently. Held never telepaths @wait (its pause rides the
// say, released by @ok).
public sealed class AilmentSyncEngineTests
{
    private sealed class Harness : IDisposable
    {
        public MessageStore Messages { get; } = new();
        public ConditionTracker Tracker { get; }
        public PartyState Party { get; } = new();
        public PartyRestSync Rest { get; }
        public AilmentSyncEngine Engine { get; }
        public SpellsSettings Spells { get; set; } = new();

        /// <summary>Ailment flags the player has a cure spell configured for.</summary>
        public MessageFlags CureConfigured { get; set; } = MessageFlags.None;

        /// <summary>Say-channel wire (engine's own sender).</summary>
        public List<string> Say { get; } = new();

        /// <summary>Telepath wire (@wait / @ok via PartyRestSync).</summary>
        public List<string> Telepath { get; } = new();

        public Harness()
        {
            Tracker = new ConditionTracker(Messages, null);
            Rest = new PartyRestSync(Party);
            Rest.SetWireSender(b => Telepath.Add(Encoding.Latin1.GetString(b)));
            Engine = new AilmentSyncEngine(
                Tracker, Rest, () => Spells,
                isInParty: () => Party.IsInParty,
                hasCureConfigured: f => (CureConfigured & f) != MessageFlags.None,
                log: null);
            Engine.SetWireSender(b => Say.Add(Encoding.Latin1.GetString(b)));

            // Follower in a party so @wait / @ok can fire.
            Party.IsInParty = true;
            Party.LeaderName = "Leader";
        }

        public void Feed(string text)
        {
            var emitted = new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            typeof(ConditionTracker)
                .GetMethod("OnLine",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .Invoke(Tracker, new object[] { emitted });
        }

        public void Dispose()
        {
            Engine.Dispose();
            Rest.Dispose();
            Tracker.Dispose();
        }
    }

    private static MessageRecord Ailment(string name, MessageFlags flags, string applied, string ends) =>
        new(
            Id: MessageRecord.ComputeId(name, "", "", "", applied, ends),
            Name: name,
            Flags: flags,
            RawFlagsHex: (ushort)flags,
            CasterMessage: string.Empty,
            TargetMessage: string.Empty,
            WitnessMessage: string.Empty,
            AppliedMessage: applied,
            AppliedEndsWith: ends);

    private static void SeedAll(Harness h)
    {
        h.Messages.Messages.Add(Ailment("Poison",  MessageFlags.Poisoned, "poisoned!", "poison wears off."));
        h.Messages.Messages.Add(Ailment("Blind",   MessageFlags.Blinded,  "blinded!",  "vision returns."));
        h.Messages.Messages.Add(Ailment("Confuse", MessageFlags.Confused, "confused!", "head clears."));
        h.Messages.Messages.Add(Ailment("Disease", MessageFlags.Diseased, "diseased!", "disease fades."));
        h.Messages.Messages.Add(Ailment("Held",    MessageFlags.MovementPrevented, "cannot move!", "can move again."));
    }

    [Fact]
    public void Poisoned_WaitsButDoesNotSay()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been poisoned!");

        // Poison is NOT announced verbosely — an observer reads it from the par
        // `P` flag. It still telepaths its @wait to the leader.
        Assert.Empty(h.Say);
        Assert.Equal("/Leader @wait\r", Assert.Single(h.Telepath));
    }

    [Theory]
    [InlineData("blinded!",  ".@blind\r")]
    [InlineData("confused!", ".@confused\r")]
    [InlineData("diseased!", ".@diseased\r")]
    public void EachAilment_UsesItsSayToken(string applied, string expected)
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You are " + applied);

        Assert.Equal(expected, Assert.Single(h.Say));
    }

    [Fact]
    public void DoNotAnnounce_SuppressesSay_ButWaitStillFires()
    {
        // Uses blindness (a VERBOSE ailment) — poison no longer exercises the say
        // path, so the DoNotAnnounce<X> gate is shown against a token that does say.
        using Harness h = new();
        SeedAll(h);
        h.Spells = new SpellsSettings { DoNotAnnounceBlindness = true };

        h.Feed("You have been blinded!");

        Assert.Empty(h.Say);
        Assert.Equal("/Leader @wait\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void Ignore_SuppressesWait_ButSayStillFires()
    {
        // Blindness is verbose, so its say fires even when its @wait is ignored.
        using Harness h = new();
        SeedAll(h);
        h.Spells = new SpellsSettings { IgnoreBlindness = true };

        h.Feed("You have been blinded!");

        Assert.Equal(".@blind\r", Assert.Single(h.Say));
        Assert.Empty(h.Telepath);
    }

    [Fact]
    public void Cleared_AfterWait_SendsOk()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been poisoned!");
        h.Feed("The poison wears off.");

        // @wait then @ok on the telepath channel.
        Assert.Equal(new[] { "/Leader @wait\r", "/Leader @ok\r" }, h.Telepath);
        // Poison is not announced on say (observers read the par `P` flag) — so
        // nothing lands on the say wire on apply or clear.
        Assert.Empty(h.Say);
    }

    [Fact]
    public void Cleared_SendsBareApplyOnly_NoOffOnSay()
    {
        // Blindness (verbose) with its @wait ignored: the bare apply token is the
        // ONLY say that ever goes out — there's no '.@blind off' on clear (MegaMUD
        // parity). A receiver clears the chip via cure / duration / @status instead.
        using Harness h = new();
        SeedAll(h);
        h.Spells = new SpellsSettings { IgnoreBlindness = true };

        h.Feed("You have been blinded!");
        h.Feed("Your vision returns.");

        Assert.Empty(h.Telepath);
        Assert.Equal(new[] { ".@blind\r" }, h.Say);   // apply only, no off
    }

    [Fact]
    public void TwoAilments_OneWait_OkOnlyWhenBothClear()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been poisoned!");
        h.Feed("You have been blinded!");
        // Two ailments, but a single @wait holds the leader.
        Assert.Single(h.Telepath);

        h.Feed("The poison wears off.");
        Assert.Single(h.Telepath);   // blind still holds

        h.Feed("Your vision returns.");
        Assert.Equal(new[] { "/Leader @wait\r", "/Leader @ok\r" }, h.Telepath);
    }

    [Fact]
    public void Held_AnnouncesSay_ButNeverTelepathsWait()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You cannot move!");

        // Held broadcasts a bare '.@held' on set (MegaMUD parity — no 'on'/'off'
        // suffix) and never telepaths @wait: the leader-pause is driven by that say
        // on the receiving side.
        Assert.Equal(".@held\r", Assert.Single(h.Say));
        Assert.Empty(h.Telepath);
    }

    [Fact]
    public void Held_Cleared_SendsNoSayOff_ButSendsOk()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You cannot move!");
        h.Feed("You can move again.");

        // No '.@held off' on clear — MegaMUD sends no 'off' token. The bare '.@held'
        // apply is the only say that ever goes out; observers clear the HELD badge via
        // a witnessed cure, the spell-data duration timing out, or a @status reconcile
        // (reports paradigm-20260820-122200 / -153540). The @ok still balances the
        // say-driven leader pause.
        Assert.Equal(new[] { ".@held\r" }, h.Say);
        Assert.Equal("/Leader @ok\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void HeldThenPoisoned_OkOnlyWhenBothClear()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You cannot move!");       // silent Held reason, .@held say
        h.Feed("You have been poisoned!"); // poison: no say (par-driven) + (suppressed) @wait — leader already paused via @held
        Assert.Empty(h.Telepath);          // no @wait telepath yet (Held holds the 0→1 slot silently)

        h.Feed("You can move again.");      // Held clears; poison still holds
        Assert.Empty(h.Telepath);

        h.Feed("The poison wears off.");    // last reason clears → @ok
        Assert.Equal("/Leader @ok\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void NotInParty_SuppressesSay()
    {
        using Harness h = new();
        SeedAll(h);
        h.Party.IsInParty = false;

        h.Feed("You have been poisoned!");

        // Out of a party there's no one to tell — no say, and CanSignal also
        // blocks the @wait on the wire.
        Assert.Empty(h.Say);
        Assert.Empty(h.Telepath);
    }

    [Fact]
    public void CureConfigured_SuppressesSay_ButWaitStillFires()
    {
        using Harness h = new();
        SeedAll(h);
        h.CureConfigured = MessageFlags.Poisoned;   // we self-cure poison

        h.Feed("You have been poisoned!");

        // We can clear it ourselves, so no broadcast — but the @wait still
        // pauses the leader while we cast (the cure gate is say-only).
        Assert.Empty(h.Say);
        Assert.Equal("/Leader @wait\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void CureHoldsConfigured_SuppressesHeldSay_AndNoOk()
    {
        using Harness h = new();
        SeedAll(h);
        h.CureConfigured = MessageFlags.MovementPrevented;  // we self-cure holds

        h.Feed("You cannot move!");
        h.Feed("You can move again.");

        // Self-cure: nothing announced, and because the Held reason is only
        // registered when we announce, there's no @ok either.
        Assert.Empty(h.Say);
        Assert.Empty(h.Telepath);
    }

    [Fact]
    public void NoSayWireSender_DoesNotThrow()
    {
        // Engine with no say sender bound — the announce path must no-op
        // silently rather than NRE.
        MessageStore messages = new();
        ConditionTracker tracker = new(messages, null);
        PartyState party = new();
        PartyRestSync rest = new(party);
        party.IsInParty = true;
        party.LeaderName = "Leader";
        using AilmentSyncEngine engine = new(
            tracker, rest, () => new SpellsSettings(),
            isInParty: () => party.IsInParty,
            hasCureConfigured: _ => false,
            log: null);
        messages.Messages.Add(Ailment("Poison", MessageFlags.Poisoned, "poisoned!", "poison wears off."));

        var emitted = new LineExtractor.EmittedLine(
            "You have been poisoned!", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false);
        typeof(ConditionTracker)
            .GetMethod("OnLine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(tracker, new object[] { emitted });

        tracker.Dispose();
        rest.Dispose();
    }

    // ===== Mid-affliction Ignore-toggle reconcile (ReevaluateWaits) =====
    //
    // OnConditionsChanged latches the @wait decision at onset. Toggling an
    // Ignore<X> gate while still afflicted must reconcile the standing wait —
    // the live report was "flip IgnorePoison on while poisoned and the party
    // never resumes". SpellsSectionViewModel.Apply calls ReevaluateWaits after
    // pushing the new settings.

    [Fact]
    public void ReevaluateWaits_TogglingIgnoreOnMidPoison_ReleasesWait()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been poisoned!");
        Assert.Equal("/Leader @wait\r", Assert.Single(h.Telepath));

        // Flip IgnorePoison ON while still poisoned. Without the reconcile the
        // already-telepathed @wait stands and the leader is stuck.
        h.Spells.IgnorePoison = true;
        h.Engine.ReevaluateWaits();

        Assert.Equal(new[] { "/Leader @wait\r", "/Leader @ok\r" }, h.Telepath);
    }

    [Fact]
    public void ReevaluateWaits_TogglingIgnoreOffMidPoison_PlacesWait()
    {
        using Harness h = new();
        SeedAll(h);
        h.Spells = new SpellsSettings { IgnorePoison = true };

        h.Feed("You have been poisoned!");
        Assert.Empty(h.Telepath);   // ignored at onset — say fired, no @wait

        // Flip IgnorePoison OFF while still poisoned → (re)place the wait.
        h.Spells.IgnorePoison = false;
        h.Engine.ReevaluateWaits();

        Assert.Equal("/Leader @wait\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void ReevaluateWaits_NoSettingChange_IsNoOpOnWire()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been poisoned!");
        Assert.Single(h.Telepath);   // the onset @wait

        // Reconciling without toggling anything must not double-@wait or @ok —
        // PartyRestSync dedupes the standing reason.
        h.Engine.ReevaluateWaits();

        Assert.Single(h.Telepath);
    }
}
