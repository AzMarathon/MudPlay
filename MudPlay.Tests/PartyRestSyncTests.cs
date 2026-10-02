using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class PartyRestSyncTests
{
    private static (PartyRestSync sync, PartyState party, List<byte[]> wire) Setup()
    {
        PartyState party = new();
        PartyRestSync sync = new(party);
        List<byte[]> wire = new();
        sync.SetWireSender(wire.Add);
        return (sync, party, wire);
    }

    private static string LastWire(List<byte[]> w) => Encoding.Latin1.GetString(w[^1]);

    // ===== engine-callable emit side =====
    // Per user direction: PartyRestSync no longer auto-fires on
    // PlayerState.Position changes. A user manually typing `rest`
    // should NOT auto-broadcast @wait — only engines that decide
    // the leader should pause / resume call RequestWait / RequestOk
    // (e.g. Phase 12 HealthManager auto-rest, message-engine flag
    // triggers on paralyze / held / confused, etc.).

    [Fact]
    public void RequestWait_Solo_SendsNothing()
    {
        var (sync, party, wire) = Setup();
        Assert.False(party.IsInParty);
        sync.RequestWait(WaitReason.Health);
        Assert.Empty(wire);
    }

    [Fact]
    public void RequestWait_AsLeader_SendsNothing()
    {
        // Leaders don't @wait themselves.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.SelfIsLeader = true;
        party.LeaderName = "Forged";
        sync.RequestWait(WaitReason.Health);
        Assert.Empty(wire);
    }

    [Fact]
    public void RequestWait_NoLeaderName_SendsNothing()
    {
        // No leader name means we don't know where to telepath.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = null;
        sync.RequestWait(WaitReason.Health);
        Assert.Empty(wire);
    }

    [Fact]
    public void RequestWait_AsFollower_TelepathsLeader()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Health);
        Assert.Equal("/Leader @wait\r", LastWire(wire));
    }

    [Fact]
    public void RequestOk_AfterWait_AsFollower_TelepathsLeader()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Health);
        sync.RequestOk(WaitReason.Health);
        Assert.Equal("/Leader @ok\r", LastWire(wire));
    }

    [Fact]
    public void RequestOk_WithoutPriorWait_SendsNothing()
    {
        // @ok only balances a held reason — releasing a reason that was
        // never placed must not leak a spurious @ok.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestOk(WaitReason.Health);
        Assert.Empty(wire);
    }

    // ===== @ok outcome and the held-reason set =====
    // The one way this protocol fails is a reason that never clears: the set
    // never empties, every later @ok is suppressed, and the party stays stopped.
    // RequestOk reports whether it actually sent, and HeldReasons names what is
    // still holding, so that state is visible instead of silent.

    [Fact]
    public void RequestOk_ReturnsTrueOnlyWhenItReachesTheWire()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";

        sync.RequestWait(WaitReason.Health);
        sync.RequestWait(WaitReason.Poison);

        Assert.False(sync.RequestOk(WaitReason.Health));   // Poison still holds
        Assert.Single(wire);                               // nothing new on the wire
        Assert.True(sync.RequestOk(WaitReason.Poison));    // last one out
        Assert.Equal("/Leader @ok\r", LastWire(wire));
    }

    [Fact]
    public void RequestOk_ForAReasonNeverPlaced_ReturnsFalse()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        Assert.False(sync.RequestOk(WaitReason.Held));
        Assert.Empty(wire);
    }

    [Fact]
    public void HeldReasons_NamesWhatIsKeepingTheLeaderPaused()
    {
        // This is the diagnosis a wedged party needs: @wait went out, no @ok
        // followed, and this says which reason is the one that never cleared.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";

        Assert.Empty(sync.HeldReasons);
        sync.RequestWait(WaitReason.Blindness);
        sync.RequestWait(WaitReason.Poison);
        // OrderBy on the enum is DECLARATION order, so Poison (1) precedes
        // Blindness (2) — not alphabetical.
        Assert.Equal(new[] { WaitReason.Poison, WaitReason.Blindness },
                     sync.HeldReasons.OrderBy(r => r).ToArray());

        sync.RequestOk(WaitReason.Blindness);
        Assert.Equal(new[] { WaitReason.Poison }, sync.HeldReasons.ToArray());
        Assert.True(sync.IsHoldingWait);

        sync.RequestOk(WaitReason.Poison);
        Assert.Empty(sync.HeldReasons);
        Assert.False(sync.IsHoldingWait);
    }

    [Fact]
    public void AStuckReason_SuppressesEveryLaterOk_WhichIsTheWedge()
    {
        // Documents the failure mode rather than papering over it. A reason that
        // never clears (here: Held, whose condition flag never went away) means a
        // later Health wait/release cycle sends NOTHING, and the leader is paused
        // indefinitely. No auto-release: it would tell the leader to move off
        // while the follower genuinely still cannot.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";

        sync.RequestWait(WaitReason.Held);        // never released
        Assert.Single(wire);

        for (int i = 0; i < 3; i++)
        {
            sync.RequestWait(WaitReason.Health);
            Assert.False(sync.RequestOk(WaitReason.Health));
        }
        Assert.Single(wire);                       // not one @ok in three cycles
        Assert.Equal(new[] { WaitReason.Held }, sync.HeldReasons.ToArray());

        Assert.True(sync.RequestOk(WaitReason.Held));   // ...and it recovers at once
        Assert.Equal("/Leader @ok\r", LastWire(wire));
    }

    // ===== MegaMUD wait reasons on the wire =====
    // MegaMUD telepaths a parenthetical reason after the token — "@wait (HP's
    // too low)" — and MudPlay used to send one for TooHeavy only. These pin the
    // string each reason carries, because the leader DISPLAYS it and a wedged
    // wait is diagnosed from it.

    [Theory]
    [InlineData(WaitReason.Poison,    "(waiting on message condition)")]
    [InlineData(WaitReason.Disease,   "(waiting on message condition)")]
    [InlineData(WaitReason.Blindness, "(blinded)")]
    [InlineData(WaitReason.Confusion, "(confused)")]
    [InlineData(WaitReason.Held,      "(can't move)")]
    [InlineData(WaitReason.TooHeavy,  "(too heavy to move)")]
    public void RequestWait_CarriesTheReason(WaitReason reason, string note)
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(reason);
        Assert.Equal($"/Leader @wait {note}\r", LastWire(wire));
    }

    [Fact]
    public void RequestWait_Health_IsBareUnlessTheCallerSaysWhichPool()
    {
        // Health covers BOTH pools, so PartyRestSync cannot know which tripped
        // and deliberately has no default for it — HealthManager passes
        // HpNote / ManaNote from its own gate flags (HealthManager.PartyWaitNote).
        // A bare Health wait is correct, not a regression.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Health);
        Assert.Equal("/Leader @wait\r", LastWire(wire));
    }

    [Theory]
    [InlineData("(HP's too low)")]
    [InlineData("(mana's too low)")]
    public void RequestWait_Health_WithAPoolNote_SendsIt(string note)
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Health, note: note);
        Assert.Equal($"/Leader @wait {note}\r", LastWire(wire));
    }

    [Fact]
    public void RequestWait_ExplicitNote_BeatsTheDefault()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Held, note: "(pinned down)");
        Assert.Equal("/Leader @wait (pinned down)\r", LastWire(wire));
    }

    [Fact]
    public void SecondReason_IsDedupedSoItsNoteNeverReachesTheWire()
    {
        // Only the 0→non-empty transition telepaths, so the FIRST reason's note
        // is the one the leader sees. A second reason arriving is silent, which
        // is why the leader can be told "(blinded)" while a poison is also held.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Blindness);
        sync.RequestWait(WaitReason.Poison);
        Assert.Single(wire);
        Assert.Equal($"/Leader @wait {PartyRestSync.BlindNote}\r", LastWire(wire));
    }

    [Fact]
    public void EveryReasonExceptHealth_HasANote()
    {
        // A reason added to the enum without a note would silently go out bare,
        // which is the state this change exists to end. Health is the one
        // deliberate exception (see above).
        foreach (WaitReason r in Enum.GetValues<WaitReason>())
        {
            var (sync, party, wire) = Setup();
            party.IsInParty = true;
            party.LeaderName = "Leader";
            sync.RequestWait(r);
            string sent = LastWire(wire);
            if (r == WaitReason.Health) continue;
            Assert.True(sent.Contains('('),
                $"{r} telepathed a bare @wait: give it a note in PartyRestSync.DefaultNote");
        }
    }

    [Fact]
    public void TwoReasons_SendOneWait_OkOnlyWhenLastClears()
    {
        // Health + Poison both hold the wait. @wait fires once (on the
        // first reason); @ok fires only when the LAST reason releases.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";

        sync.RequestWait(WaitReason.Health);
        sync.RequestWait(WaitReason.Poison);
        Assert.Single(wire);                       // only one @wait
        Assert.Equal("/Leader @wait\r", LastWire(wire));

        sync.RequestOk(WaitReason.Health);
        Assert.Single(wire);                       // Poison still holds — no @ok yet

        sync.RequestOk(WaitReason.Poison);
        Assert.Equal(2, wire.Count);
        Assert.Equal("/Leader @ok\r", LastWire(wire));
    }

    [Fact]
    public void Resend_ReTelepathsAWaitAlreadyHeld_AndStillOnlyOneOk()
    {
        // The leader may have timed out the first wait; a resend re-asks. The reason
        // is still tracked once, so a single @ok balances it.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";

        sync.RequestWait(WaitReason.Health, resend: true);
        sync.RequestWait(WaitReason.Health, resend: true);
        Assert.Equal(2, wire.Count);
        Assert.Equal("/Leader @wait\r", LastWire(wire));

        sync.RequestOk(WaitReason.Health);
        Assert.Equal("/Leader @ok\r", LastWire(wire));
    }

    [Fact]
    public void Resend_Solo_SendsNothing()
    {
        var (sync, _, wire) = Setup();
        sync.RequestWait(WaitReason.Health, resend: true);
        Assert.Empty(wire);
    }

    [Fact]
    public void DuplicateWaitReason_SendsOneWait()
    {
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestWait(WaitReason.Poison);
        sync.RequestWait(WaitReason.Poison);
        Assert.Single(wire);
    }

    [Fact]
    public void RequestWait_TelepathsByGivenName_NotFullDisplayName()
    {
        // "Leader Lastname" leader → /Leader (given only) — MajorMUD
        // rejects "Given Family" recipients for telepaths.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader Lastname";
        sync.RequestWait(WaitReason.Health);
        Assert.Equal("/Leader @wait\r", LastWire(wire));
    }

    [Fact]
    public void RequestWait_NoWireSender_NoThrow()
    {
        PartyState party = new() { IsInParty = true, LeaderName = "Leader" };
        PartyRestSync sync = new(party);
        // No SetWireSender — call should silently no-op.
        sync.RequestWait(WaitReason.Health);
        sync.Dispose();
    }

    // ===== RequestHeal — follower flee-substitute broadcast =====

    [Fact]
    public void RequestHeal_AsFollower_BroadcastsGangHeal()
    {
        // Broadcasts to the party (gangpath), not a leader telepath, since the
        // healer may be any member. Leader name is irrelevant to the send.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        sync.RequestHeal();
        Assert.Equal("bg @heal\r", LastWire(wire));
    }

    [Fact]
    public void RequestHeal_Solo_SendsNothing()
    {
        var (sync, party, wire) = Setup();
        Assert.False(party.IsInParty);
        sync.RequestHeal();
        Assert.Empty(wire);
    }

    [Fact]
    public void RequestHeal_AsLeader_SendsNothing()
    {
        // The leader owns the party's run decision and has no healer to ping.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.SelfIsLeader = true;
        sync.RequestHeal();
        Assert.Empty(wire);
    }

    [Fact]
    public void RequestHeal_NoWireSender_NoThrow()
    {
        PartyState party = new() { IsInParty = true, LeaderName = "Leader" };
        PartyRestSync sync = new(party);
        sync.RequestHeal();   // no SetWireSender — silent no-op
        sync.Dispose();
    }

    [Fact]
    public void PositionChange_DoesNotAutoFire()
    {
        // Regression guard for the live-test feedback: manual `rest`
        // (which sets PlayerState.Position = Resting) must NOT trigger
        // @wait. PartyRestSync no longer subscribes to PlayerState at
        // all; engines drive it explicitly.
        var (sync, party, wire) = Setup();
        party.IsInParty = true;
        party.LeaderName = "Leader";
        // Just mutating a PlayerState would have fired the old code;
        // we don't even construct one here because the new ctor
        // doesn't take it. The point of this test is to document the
        // contract.
        Assert.Empty(wire);
    }
}

public sealed class PartyEssentialHandlersPauseGateTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (RemoteCommandManager engine, PartyEssentialHandlers handlers, PartyState party) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        PlayerState player = new();
        RemoteCommandManager engine = new(chat, party, players);
        PartyEssentialHandlers handlers = new(engine, player, party);
        return (engine, handlers, party);
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void SeedPartyMember(PartyState p, string name)
    {
        p.Members.Add(new PartyMember { Name = name });
        p.IsInParty = true;
    }

    [Fact]
    public void IsPaused_IsFalseByDefault()
    {
        var (_, handlers, _) = Setup();
        Assert.False(handlers.IsPaused);
    }

    [Fact]
    public void IsPaused_GoesTrueOnWait_FiresChangedEvent()
    {
        var (engine, handlers, party) = Setup();
        SeedPartyMember(party, "Follower");
        List<bool> events = new();
        handlers.PauseGateChanged += events.Add;

        engine.DispatchForTests(Telepath("Follower", "@wait"));

        Assert.True(handlers.IsPaused);
        Assert.Equal(new[] { true }, events);
    }

    [Fact]
    public void IsPaused_StaysTrueOnSecondWait_NoExtraEvent()
    {
        var (engine, handlers, party) = Setup();
        SeedPartyMember(party, "FollowerA");
        SeedPartyMember(party, "FollowerB");
        List<bool> events = new();
        handlers.PauseGateChanged += events.Add;

        engine.DispatchForTests(Telepath("FollowerA", "@wait"));
        engine.DispatchForTests(Telepath("FollowerB", "@wait"));

        Assert.True(handlers.IsPaused);
        Assert.Single(events); // edge-only — only one transition fired
    }

    [Fact]
    public void IsPaused_GoesFalseOnlyWhenLastOkArrives()
    {
        var (engine, handlers, party) = Setup();
        SeedPartyMember(party, "FollowerA");
        SeedPartyMember(party, "FollowerB");
        engine.DispatchForTests(Telepath("FollowerA", "@wait"));
        engine.DispatchForTests(Telepath("FollowerB", "@wait"));
        List<bool> events = new();
        handlers.PauseGateChanged += events.Add;

        engine.DispatchForTests(Telepath("FollowerA", "@ok"));
        Assert.True(handlers.IsPaused);   // FollowerB still waiting
        Assert.Empty(events);

        engine.DispatchForTests(Telepath("FollowerB", "@ok"));
        Assert.False(handlers.IsPaused);
        Assert.Equal(new[] { false }, events);
    }
}
