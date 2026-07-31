using System.Collections.Generic;
using System.IO;
using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// The death-halt bridge: on our own death it asserts
/// <see cref="MovementCoordinator.UserGate"/> so every movement engine stops and
/// we sit in the graveyard until a manual resume, flavouring the chip via
/// <see cref="PlayerDeathMovementHalt.HaltedForDeath"/>. It rides
/// <see cref="RoomTracker.PlayerDeathObserved"/>, which fires for BOTH death
/// phrasings, so a miracle-save death ("You have N lives left.") halts as surely
/// as a plain "slain by" one.
/// </summary>
public sealed class PlayerDeathMovementHaltTests
{
    private sealed class Harness : IDisposable
    {
        private const string GraphJson = """
        [
          { "Number": 1, "Name": "Start", "Map": 1, "Light": 0, "Shop": 0,
            "Lair": "", "Delay": 5,
            "N": "2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Number": 2, "Name": "Graveyard", "Map": 1, "Light": 0, "Shop": 0,
            "Lair": "", "Delay": 5,
            "N": "0", "S": "1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

        private readonly string _root;
        public RoomTracker Tracker { get; }
        public MovementCoordinator Coord { get; } = new();
        public PlayerDeathMovementHalt Halt { get; }
        public int FlavourChanges { get; private set; }
        public List<byte[]> Sent { get; } = new();

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(), "fujinterm-deathhalt-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");

            Tracker = new RoomTracker(graph);
            Halt = new PlayerDeathMovementHalt(Tracker, Coord);
            Halt.SetWireSender(Sent.Add);
            Halt.HaltedForDeathChanged += () => FlavourChanges++;
        }

        // A miracle-save death — the phrasing that DeathLineWatcher's "slain by"
        // watcher never sees. RoomTracker.NoteDeath fires PlayerDeathObserved
        // regardless of phrasing.
        public void Die() => Tracker.NoteDeath(6, "You have 6 lives left.");

        public bool SentCarriageReturn =>
            Sent.Exists(b => Encoding.Latin1.GetString(b) == "\r");

        public void Dispose()
        {
            Halt.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void FreshBridge_NotHaltedNotPaused()
    {
        using Harness h = new();
        Assert.False(h.Halt.HaltedForDeath);
        Assert.False(h.Coord.IsPaused);
        Assert.DoesNotContain(MovementCoordinator.UserGate, h.Coord.AssertedGates);
    }

    [Fact]
    public void Death_AssertsUserGateAndHalts()
    {
        using Harness h = new();
        h.Die();

        Assert.True(h.Halt.HaltedForDeath);
        Assert.True(h.Coord.IsPaused);
        Assert.Contains(MovementCoordinator.UserGate, h.Coord.AssertedGates);
    }

    [Fact]
    public void Death_TagsAsserterInHistory()
    {
        using Harness h = new();
        h.Die();

        GateTransitionEntry entry = h.Coord.History.Single(e =>
            e.Gate == MovementCoordinator.UserGate && e.Asserted);
        Assert.Equal(PlayerDeathMovementHalt.AsserterName, entry.Asserter);
    }

    [Fact]
    public void UserResume_AutoClearsFlavour()
    {
        using Harness h = new();
        h.Die();
        Assert.True(h.Halt.HaltedForDeath);

        // A manual resume clears UserGate through any Navigation affordance.
        h.Coord.ClearGate(MovementCoordinator.UserGate);

        Assert.False(h.Halt.HaltedForDeath);
        Assert.False(h.Coord.IsPaused);
    }

    [Fact]
    public void FlavourClears_WhenUserGateGoes_EvenWithOtherGatesHeld()
    {
        // HaltedForDeath keys off UserGate specifically, not overall IsPaused —
        // a still-asserted combat gate must not keep the "recovering" flavour up.
        using Harness h = new();
        h.Coord.AssertGate(MovementCoordinator.CombatGate);
        h.Die();
        Assert.True(h.Halt.HaltedForDeath);

        h.Coord.ClearGate(MovementCoordinator.UserGate);

        Assert.False(h.Halt.HaltedForDeath);
        Assert.True(h.Coord.IsPaused); // combat still holds movement
    }

    [Fact]
    public void ManualUserPause_DoesNotFlagHalt()
    {
        // A user pause with no death behind it must read as plain "Paused",
        // never "recovering".
        using Harness h = new();
        h.Coord.AssertGate(MovementCoordinator.UserGate);

        Assert.False(h.Halt.HaltedForDeath);
    }

    [Fact]
    public void FlavourChange_FiresOnceEachDirection()
    {
        using Harness h = new();
        h.Die();                                            // false -> true
        h.Coord.ClearGate(MovementCoordinator.UserGate);    // true -> false

        Assert.Equal(2, h.FlavourChanges);
        Assert.False(h.Halt.HaltedForDeath);
    }

    [Fact]
    public void DeathWhileAlreadyPaused_StillFlagsHalt()
    {
        // Dying during a manual pause: AssertGate is idempotent (no second
        // GatesChanged), but the death still flips the flavour so the chip
        // switches from "Paused" to "Paused — recovering".
        using Harness h = new();
        h.Coord.AssertGate(MovementCoordinator.UserGate);
        h.Die();

        Assert.True(h.Halt.HaltedForDeath);
    }

    [Fact]
    public void GraveyardResync_StillPendingRespawn_SendsCr()
    {
        // Report stock-20260730-194053: after death the respawn room display can be
        // slow, leaving us "lost". If we're still un-anchored when the resync
        // fallback fires, a CR forces the graveyard to re-display so PendingRespawn's
        // candidate search can land it.
        using Harness h = new();
        h.Die();
        Assert.Equal(RoomConfidence.PendingRespawn, h.Tracker.State.Confidence);
        Assert.False(h.SentCarriageReturn);   // armed, not fired yet

        h.Halt.FireGraveyardResyncForTests();

        Assert.True(h.SentCarriageReturn);
    }

    [Fact]
    public void GraveyardResync_NoWireSender_NoThrow()
    {
        // Unbound sender (pre-connect) → the resync is a silent no-op, never a throw.
        using Harness h = new();
        var halt = new PlayerDeathMovementHalt(h.Tracker, h.Coord);   // no SetWireSender
        h.Tracker.NoteDeath(6, "You have 6 lives left.");

        halt.FireGraveyardResyncForTests();   // must not throw

        halt.Dispose();
    }

    [Fact]
    public void Dispose_StopsReactingToDeath()
    {
        using Harness h = new();
        h.Halt.Dispose();
        h.Die();

        Assert.False(h.Halt.HaltedForDeath);
        Assert.DoesNotContain(MovementCoordinator.UserGate, h.Coord.AssertedGates);
    }

    [Fact]
    public void Death_InvokesEngineStopper()
    {
        // The halt full-stops every movement engine on death (AppServices wires the
        // stopper to Walker/LoopRunner/AutoLair Stop) so no retained destination
        // survives to re-drive us back — report stock-20260731-082602.
        using Harness h = new();
        int stops = 0;
        h.Halt.SetEngineStopper(() => stops++);

        h.Die();

        Assert.Equal(1, stops);
    }

    [Fact]
    public void Death_HaltHolds_EvenWhenStopperClearsUserGate()
    {
        // Auto-Lair edge case: the user had paused (UserGate asserted) and the
        // engine stopper (AutoLair.Stop) clears the UserGate as it tears down.
        // Because the halt stops engines FIRST and asserts the gate LAST, the death
        // halt still ends asserted — nothing can re-drive us after death.
        using Harness h = new();
        h.Coord.AssertGate(MovementCoordinator.UserGate);   // pre-death manual pause
        h.Halt.SetEngineStopper(() => h.Coord.ClearGate(MovementCoordinator.UserGate));

        h.Die();

        Assert.True(h.Halt.HaltedForDeath);
        Assert.Contains(MovementCoordinator.UserGate, h.Coord.AssertedGates);
    }
}
