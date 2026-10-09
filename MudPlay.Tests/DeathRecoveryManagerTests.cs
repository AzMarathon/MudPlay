using System.IO;
using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Pins <see cref="DeathRecoveryManager"/>'s stock corpse-recovery flow: on
/// re-entering the death room, auto-recover reads the "You notice" survey and, if
/// our "corpse of &lt;given-name&gt;" is on the floor, sends ONE
/// <c>recover corpse &lt;name&gt;</c>; the single "You have recovered the corpse
/// of &lt;name&gt;." line finalises. When the corpse is NOT in the survey the pile
/// is marked Missing (terminal) so it never spam-retries. (The <c>@comeback</c>
/// party-pickup flow is a separate concern owned by
/// <see cref="MudPlay.Game.Remote.PartyComebackManager"/>.)
/// </summary>
public sealed class DeathRecoveryManagerTests
{
    private sealed class GraphHarness : IDisposable
    {
        private readonly string _root;
        private readonly MessageRouter _router;
        public ProfileService Profile { get; } = new();
        public RoomTracker Tracker { get; }
        public DeathLineWatcher Watcher { get; }
        public GroundItemTracker Ground { get; }
        public DeathRecoveryManager Recovery { get; }
        public List<string> Sent { get; } = new();
        public InventorySnapshot Snapshot { get; set; } = InventorySnapshot.Empty;

        // Combat-interleave spies: Hostiles drives the hostilesPresent probe,
        // ResumeArmed counts NoteBetweenRoundCast nudges, GateHeld mirrors the
        // CorpseRecovery gate, Ac backs the ArmourClass lookup.
        public bool Hostiles { get; set; }
        public int ResumeArmed { get; private set; }
        public bool GateHeld { get; private set; }
        public Dictionary<string, int> Ac { get; } = new();
        // Realm probe: true = Paradigm (corpse), false = Stock (loose ground items).
        // Defaults Paradigm so the existing corpse tests keep their behaviour.
        public bool Paradigm { get; set; } = true;
        public void CombatRound() => Recovery.OnRecoveryCombatRound();
        public void Heartbeat() => Recovery.OnRecoveryHeartbeat();

        private const string GraphJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "1/3", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "North Square",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;

        // Stock spill-sweep wiring. Graph is the room graph the sweep plans from;
        // Walker (when asked for) is the real walker, its moves landing in Sent beside
        // recovery's own commands; Held stands in for a movement gate; Stays and
        // StashRooms back the two probes.
        public RoomGraphManager Graph { get; }
        public AutoWalkManager? Walker { get; }
        public MovementCoordinator Coordinator { get; } = new();
        public AvoidFilter Filter { get; } = new();
        public bool Held { get; set; }
        public HashSet<string> Stays { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<RoomKey> StashRooms { get; } = new();

        public GraphHarness(string characterName = "Ermias", string graphJson = GraphJson, bool withWalker = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-deathrec-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), graphJson);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            Graph = graph;

            _router = new MessageRouter();
            DefaultPatterns.Seed(_router);
            LogService log = new();
            Tracker = new RoomTracker(graph);
            Tracker.AttachInventorySnapshot(() => Snapshot);
            Watcher = new DeathLineWatcher(_router, log);
            Ground = new GroundItemTracker(_router, new CurrencyNaming());
            Recovery = new DeathRecoveryManager(Watcher, Profile, Tracker, log);
            Recovery.AttachGroundItems(Ground);
            Recovery.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
            Recovery.AttachInventorySnapshot(() => Snapshot);
            Recovery.AttachCombatInterleave(
                () => Hostiles,
                () => ResumeArmed++,
                () => GateHeld = true,
                () => GateHeld = false,
                name => Ac.TryGetValue(name, out int v) ? v : 0);
            Recovery.SetRealmProbe(() => Paradigm);
            Recovery.AttachSpillSweep(graph.GetRoom, () => Held, StashRooms.Contains);
            Recovery.SetStaysOnDeathProbe(Stays.Contains);
            // As the app wires it, after recovery's own subscription: the floor list
            // belongs to the room just left, so a genuine room change empties it.
            Tracker.StateChanged += t =>
            {
                if (t.NewRoom is null || (t.PreviousRoom is not null && t.PreviousRoom.Key.Equals(t.NewRoom.Key))) return;
                Ground.OnRoomChanged();
            };
            if (withWalker)
            {
                Walker = new AutoWalkManager(graph, new BfsMapper(graph), Tracker, Coordinator, Filter);
                Walker.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
                Recovery.AttachWalker(Walker);
            }

            Profile.LoadBlank();
            Profile.Current!.Name = characterName;
            Tracker.Hydrate(Profile.Current!);
        }

        private static RoomObservation Obs(string name, params Direction[] exits)
            => new(name, new HashSet<Direction>(exits));

        // Confirm at Town Gates (1/1) — both initial landing and re-entry.
        public void EnterGates() => Tracker.NoteRoomObserved(Obs("Town Gates", Direction.N));

        // Feed a room floor survey ("You notice … here.") to the GroundItemTracker,
        // as the room display would after entry — this is what drives the corpse grab.
        public void FeedSurvey(string list) =>
            _router.Dispatch(new LineExtractor.EmittedLine(
                $"You notice {list} here.", Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));

        public DeathRecord Latest => Profile.Current!.DeathHistory![^1];

        public void Dispose()
        {
            Recovery.Dispose();
            Ground.Dispose();
            Watcher.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static InventorySnapshot SnapWith(EquippedItem[] worn, string[] carried)
        => new(CurrencyHoldings.Empty, EncumbranceReading.Empty, worn, carried, DateTimeOffset.UtcNow);

    private static void Die(GraphHarness h, EquippedItem[] worn, string[] carried)
    {
        h.EnterGates();
        h.Snapshot = SnapWith(worn, carried);
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");   // tracker → PendingRespawn
        h.Sent.Clear();
    }

    [Fact]
    public void ReEntry_AutoRecover_CorpsePresent_RecoversViaCorpseCommand()
    {
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("rusty dagger", "Weapon Hand") }, new[] { "torch" });
        h.Recovery.AutoRecover = true;

        h.EnterGates();                                    // walk back into the death room → armed
        Assert.Empty(h.Sent);                              // nothing sent until the survey lands
        h.FeedSurvey("corpse of Ermias");                  // the floor survey shows our corpse

        Assert.Contains("recover corpse Ermias", h.Sent);
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("get ")); // NOT the old per-item get spam
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.Recovery.FeedTestLine("You have recovered the corpse of Ermias.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void CorpseMatch_UsesLiveName_NotStaleProfileName()
    {
        // report stock-20260828-104653: a profile COPIED from another character keeps
        // the old name in CharacterProfile.Name. Corpse matching must identify self by
        // the LIVE in-game name (PartyManager.LocalCharacterName), never the stale
        // profile name — matching another player's corpse would be a mis-recovery.
        // The lone-corpse rule only auto-takes when we have NO name to match, so
        // recovering "corpse of Raijin" here proves it matched the live name, not
        // the stale "Fujin" (which would leave it unmatched → nothing sent).
        using GraphHarness h = new("Fujin");                     // stale copied-profile name
        h.Recovery.AttachLiveSelfName(() => "Raijin WuzHere");   // the real, live name
        Die(h, new[] { new EquippedItem("rusty dagger", "Weapon Hand") }, new[] { "torch" });
        h.Recovery.AutoRecover = true;

        h.EnterGates();
        h.FeedSurvey("corpse of Raijin");

        Assert.Contains("recover corpse Raijin", h.Sent);
    }

    [Fact]
    public void ReEntry_AutoRecover_CorpseAbsent_MarksMissing_SendsNothing()
    {
        // The reported bug: the pile's corpse is gone (only coins on the floor).
        // We must send NO recover/get and mark the pile Missing instead of looping.
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("padded helm", "Head") }, new[] { "club" });
        h.Recovery.AutoRecover = true;

        h.EnterGates();
        h.FeedSurvey("3 copper farthings");                // cash only — no corpse

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void MissingPile_DoesNotReArm_OnReEntry()
    {
        using GraphHarness h = new();
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.EnterGates();
        h.FeedSurvey("3 copper farthings");                // → Missing
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);

        // Leave and come back: a Missing pile must not re-arm, even if the corpse
        // now appears in the survey — no spam. (Recover Now is the explicit retry.)
        h.Tracker.NoteRoomObserved(Obs3());                // 1/3 North Square
        h.Sent.Clear();
        h.EnterGates();
        h.FeedSurvey("corpse of Ermias");
        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void ReEntry_NoAutoRecover_SendsNothing_ManualRecoverStillFinalizes()
    {
        using GraphHarness h = new();
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });

        h.EnterGates();
        h.FeedSurvey("corpse of Ermias");
        Assert.Empty(h.Sent);                              // no auto-grab without the toggle
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        // The user recovers manually; we key off the confirmation line either way.
        h.Recovery.FeedTestLine("You have recovered the corpse of Ermias.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void AutoEquip_ReequipsWornItems_OnCorpseRecovered()
    {
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("plate mail", "Torso") }, Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        h.EnterGates();
        h.FeedSurvey("corpse of Ermias");
        Assert.Contains("recover corpse Ermias", h.Sent);

        h.Recovery.FeedTestLine("You have recovered the corpse of Ermias.");
        Assert.Contains("wear plate mail", h.Sent);        // armour → wear, after the corpse is back
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    // Report paradigm-20261004-201808: Auto-All off, the corpse recovered by hand
    // with two monsters in the room, and the client fired eleven wear commands.
    [Fact]
    public void AutoEquip_WithAutoAllOff_HoldsTheGear_UntilItIsBackOn()
    {
        using GraphHarness h = new();
        bool autoOn = false;
        h.Recovery.SetAutoEnabledProbe(() => autoOn);
        Die(h, new[]
        {
            new EquippedItem("plate mail", "Torso"),
            new EquippedItem("platinum mace", "Weapon Hand"),
        }, new[] { "torch" });
        h.Recovery.AutoEquip = true;
        h.EnterGates();
        h.Sent.Clear();

        h.Recovery.FeedTestLine("You have recovered the corpse of Ermias.");   // by hand

        Assert.Empty(h.Sent);
        Assert.Equal(2, h.Recovery.HeldReequipCount);
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);

        autoOn = true;
        h.Recovery.OnAutoAllRestored();

        Assert.Equal(new[] { "eq platinum mace", "wear plate mail" }, h.Sent.ToArray());
        Assert.Equal(0, h.Recovery.HeldReequipCount);
    }

    [Fact]
    public void AutoEquip_HeldForAutoAll_LeavesOutWhatWasWornByHandMeanwhile()
    {
        using GraphHarness h = new();
        bool autoOn = false;
        h.Recovery.SetAutoEnabledProbe(() => autoOn);
        Die(h, new[]
        {
            new EquippedItem("plate mail", "Torso"),
            new EquippedItem("platinum mace", "Weapon Hand"),
        }, Array.Empty<string>());
        h.Recovery.AutoEquip = true;
        h.EnterGates();
        h.Recovery.FeedTestLine("You have recovered the corpse of Ermias.");
        h.Sent.Clear();

        // The user put the armour back on themselves while Auto-All was off.
        h.Snapshot = SnapWith(new[] { new EquippedItem("plate mail", "Torso") }, new[] { "platinum mace" });
        autoOn = true;
        h.Recovery.OnAutoAllRestored();

        Assert.Equal(new[] { "eq platinum mace" }, h.Sent.ToArray());
    }

    [Fact]
    public void AutoEquip_HeldForAutoAll_IsDroppedByANewDeath()
    {
        using GraphHarness h = new();
        bool autoOn = false;
        h.Recovery.SetAutoEnabledProbe(() => autoOn);
        Die(h, new[] { new EquippedItem("plate mail", "Torso") }, Array.Empty<string>());
        h.Recovery.AutoEquip = true;
        h.EnterGates();
        h.Recovery.FeedTestLine("You have recovered the corpse of Ermias.");
        Assert.Equal(1, h.Recovery.HeldReequipCount);

        h.Tracker.NoteDeath(1, "You now have 1 lives remaining.");

        Assert.Equal(0, h.Recovery.HeldReequipCount);
    }

    // ----- gear handed back by a party member -------------------------

    [Fact]
    public void HandedBack_AllPileItems_FinalisesAndReequipsWorn()
    {
        // report paradigm-20260926-102406: the leader recovered the follower's corpse
        // and gave the gear back; it sat unworn in the pack. Each "X just gave you …"
        // strikes an item off the pile; once the burst goes quiet the pile finalises
        // and the worn half goes back on.
        using GraphHarness h = new();
        Die(h, new[]
        {
            new EquippedItem("plate mail", "Torso"),
            new EquippedItem("platinum mace", "Weapon Hand"),
        }, new[] { "torch" });
        h.Recovery.AutoEquip = true;

        h.Recovery.OnItemReceived("plate mail", "Nineteen");
        h.Recovery.OnItemReceived("platinum mace", "Nineteen");
        h.Recovery.OnItemReceived("torch", "Nineteen");
        Assert.Empty(h.Sent);                          // still settling — nothing worn yet

        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();   // the hand-off went quiet

        Assert.Equal(new[] { "eq platinum mace", "wear plate mail" }, h.Sent.ToArray());
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Contains("Nineteen", h.Latest.RecoveryMessage);
    }

    [Fact]
    public void HandedBack_SomeItems_HoldsPartial_AndReequipsWhatCameBack()
    {
        using GraphHarness h = new();
        Die(h, new[]
        {
            new EquippedItem("plate mail", "Torso"),
            new EquippedItem("steel helm", "Head"),
        }, Array.Empty<string>());
        h.Recovery.AutoEquip = true;

        h.Recovery.OnItemReceived("plate mail", "Nineteen");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Equal(new[] { "wear plate mail" }, h.Sent.ToArray());
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Equal(new[] { "steel helm" }, h.Latest.UnrecoveredItems);
    }

    [Fact]
    public void HandedBack_ItemNotFromThePile_Ignored()
    {
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("plate mail", "Torso") }, Array.Empty<string>());
        h.Recovery.AutoEquip = true;

        h.Recovery.OnItemReceived("loaf of bread", "Nineteen");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Active, h.Latest.Status);
    }

    [Fact]
    public void HandedBack_AutoEquipOff_FinalisesWithoutWearing()
    {
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("plate mail", "Torso") }, Array.Empty<string>());
        h.Recovery.AutoEquip = false;

        h.Recovery.OnItemReceived("plate mail", "Nineteen");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void AutoEquip_ReequipsHeldWeapon_WithEq_NotHold()
    {
        // Report: corpse recovery sent "hold platinum mace" for the weapon. A held
        // item (Weapon Hand / Off-Hand) must be wielded with `eq`, matching the
        // equipment manager — `hold` only carries it in hand, it doesn't wield it.
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("platinum mace", "Weapon Hand") }, Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        h.EnterGates();
        h.FeedSurvey("corpse of Ermias");
        h.Recovery.FeedTestLine("You have recovered the corpse of Ermias.");

        Assert.Contains("eq platinum mace", h.Sent);
        Assert.DoesNotContain("hold platinum mace", h.Sent);
    }

    [Fact]
    public void GivenNameOnly_UsedForCorpseMatchAndCommand()
    {
        // The survey shows the GIVEN name only ("corpse of Ermias"), and the
        // command must use it too — never the family name.
        using GraphHarness h = new("Ermias Asghedom");
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;

        h.EnterGates();
        h.FeedSurvey("corpse of Ermias");
        Assert.Contains("recover corpse Ermias", h.Sent);
        Assert.DoesNotContain("recover corpse Ermias Asghedom", h.Sent);
    }

    [Fact]
    public void AnotherPlayersCorpse_NotRecovered_WhenNameMismatches()
    {
        using GraphHarness h = new("Ermias");
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;

        h.EnterGates();
        h.FeedSurvey("corpse of Bob");                     // someone else's corpse
        Assert.Empty(h.Sent);                              // never recover another player's corpse
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void ReEntry_KnownEmptyDeathpile_RecoveredImmediately()
    {
        using GraphHarness h = new();
        h.EnterGates();
        h.Snapshot = InventorySnapshot.Empty;   // nothing worn / carried
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");

        h.EnterGates();
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    // Recover Now away from the corpse is a walk the user asked for: it is handed to
    // the route cards like any walk-to, and the grab is armed for the arrival.
    [Fact]
    public void RecoverNow_AwayFromTheDeathRoom_GoesThroughTheRouteCards()
    {
        using GraphHarness h = new();
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        List<RoomKey> asked = new();
        h.Recovery.SetDemandedWalk(room => { asked.Add(room); return true; });

        Assert.True(h.Recovery.RecoverNow(h.Latest));

        Assert.Equal(new RoomKey(1, 1), Assert.Single(asked));

        h.EnterGates();                                    // arrived, however the cards routed it
        h.FeedSurvey("corpse of Ermias");
        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    [Fact]
    public void RecoverNow_InRoom_LooksThenRecoversOnSurvey()
    {
        using GraphHarness h = new();
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.EnterGates();                                    // auto re-entry: Partial, no grab (AutoRecover off)
        h.FeedSurvey("corpse of Ermias");
        Assert.Empty(h.Sent);                              // AutoRecover off → nothing yet

        Assert.True(h.Recovery.RecoverNow(h.Latest));
        Assert.Contains("look", h.Sent);                   // re-look to re-render the survey
        h.FeedSurvey("corpse of Ermias");                  // the look's survey
        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    [Fact]
    public void OrderForReequip_WeaponFirst_ThenArmourByHighestAc()
    {
        var worn = new List<DeathItem>
        {
            new("cloth cap", "Head"),
            new("plate mail", "Torso"),
            new("platinum mace", "Weapon Hand"),
            new("small shield", "Off-Hand"),
            new("leather boots", "Feet"),
        };
        var ac = new Dictionary<string, int>
        {
            ["cloth cap"] = 2, ["plate mail"] = 30, ["leather boots"] = 5,
        };
        List<DeathItem> ordered =
            DeathRecoveryManager.OrderForReequip(worn, n => ac.TryGetValue(n, out int v) ? v : 0);

        // Weapon Hand, then Off-Hand, then armour highest-AC-first.
        Assert.Equal(
            new[] { "platinum mace", "small shield", "plate mail", "leather boots", "cloth cap" },
            ordered.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void InCombat_Recovery_PacesReequipAcrossRounds_ThenFlushesOnRoomClear()
    {
        using GraphHarness h = new();
        var worn = new[]
        {
            new EquippedItem("platinum mace", "Weapon Hand"),
            new EquippedItem("plate mail", "Torso"),
            new EquippedItem("steel helm", "Head"),
            new EquippedItem("steel greaves", "Legs"),
            new EquippedItem("leather boots", "Feet"),
            new EquippedItem("silver ring", "Finger"),
        };
        Die(h, worn, Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;
        h.Hostiles = true;                                 // a live hostile shares the death room

        h.EnterGates();
        h.FeedSurvey("corpse of Ermias");
        Assert.Contains("recover corpse Ermias", h.Sent);  // recovery itself is unchanged (safe)

        h.Sent.Clear();
        h.Recovery.FeedTestLine("You have recovered the corpse of Ermias.");
        Assert.Empty(h.Sent);                              // NOT fired all at once — paced
        Assert.True(h.GateHeld);                           // walker held while pieces pend

        h.CombatRound();                                   // first round-gap: 4 pieces + one re-attack
        Assert.Equal(4, h.Sent.Count);
        Assert.Equal("eq platinum mace", h.Sent[0]);       // weapon first
        Assert.Equal(1, h.ResumeArmed);
        Assert.True(h.GateHeld);                           // 2 still pending

        h.Hostiles = false;                                // mob dies between rounds → room clears
        h.Heartbeat();                                     // remainder flushes at once
        Assert.Equal(6, h.Sent.Count);
        Assert.False(h.GateHeld);                          // gate released
    }

    [Fact]
    public void NoHostile_Recovery_EquipsAllAtOnce_WeaponFirst_NoGate()
    {
        using GraphHarness h = new();
        Die(h, new[]
        {
            new EquippedItem("plate mail", "Torso"),
            new EquippedItem("platinum mace", "Weapon Hand"),
        }, Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;
        // Hostiles defaults false — an empty room recovers exactly as before.

        h.EnterGates();
        h.FeedSurvey("corpse of Ermias");
        h.Sent.Clear();
        h.Recovery.FeedTestLine("You have recovered the corpse of Ermias.");

        Assert.Equal(new[] { "eq platinum mace", "wear plate mail" }, h.Sent.ToArray());
        Assert.False(h.GateHeld);                          // no gate when there's no fight to pace against
    }

    [Fact]
    public void Stock_Recovery_GetsFloorItems_NotCorpse_ThenReequipsOnceAllBack()
    {
        using GraphHarness h = new();
        Die(h,
            new[] { new EquippedItem("platinum mace", "Weapon Hand"), new EquippedItem("plate mail", "Torso") },
            new[] { "torch" });
        h.Paradigm = false;                 // Stock: items scattered loose on the floor
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        h.EnterGates();
        h.FeedSurvey("a platinum mace, a plate mail, and a torch");   // our pile on the floor

        // `get` each present pile item (article-insensitive) — NOT `recover corpse`.
        Assert.Contains("get platinum mace", h.Sent);
        Assert.Contains("get plate mail", h.Sent);
        Assert.Contains("get torch", h.Sent);
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("recover corpse"));
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.Sent.Clear();
        h.Recovery.FeedTestLine("You took a platinum mace.");
        h.Recovery.FeedTestLine("You took a plate mail.");
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);   // torch still out → not done
        h.Recovery.FeedTestLine("You took a torch.");

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        // Worn gear re-equipped once the whole pile is back — weapon first (no hostile → all at once).
        Assert.Equal(new[] { "eq platinum mace", "wear plate mail" }, h.Sent.ToArray());
    }

    [Fact]
    public void Stock_Recovery_OnlyGetsItemsPresentOnTheFloor_NoGetSpam()
    {
        // A worn helm spilled elsewhere — it is NOT in this room's survey, so we must
        // not `get` it (that was the old spam). We grab what's here and hold Partial.
        using GraphHarness h = new();
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.Paradigm = false;
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        h.EnterGates();
        h.FeedSurvey("an iron sword");       // only the sword is here; the helm spilled

        Assert.Contains("get iron sword", h.Sent);
        Assert.DoesNotContain("get steel helm", h.Sent);   // absent → never `get`-spammed
        h.Recovery.FeedTestLine("You took an iron sword.");
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);   // helm still out → Partial
    }

    [Fact]
    public void Stock_Recovery_StackedItem_GetsBareNamePerUnit_NotBatched()
    {
        using GraphHarness h = new();
        Die(h, Array.Empty<EquippedItem>(), new[] { "3 torch" });   // captured as a stack
        h.Paradigm = false;
        h.Recovery.AutoRecover = true;

        h.EnterGates();
        h.FeedSurvey("3 torch");

        // Stock has no batched `get N item` — send bare `get torch`, once per unit.
        Assert.Equal(3, h.Sent.Count(s => s == "get torch"));
        Assert.DoesNotContain(h.Sent, s => s.Contains("get 3 torch"));

        h.Recovery.FeedTestLine("You took torch.");
        h.Recovery.FeedTestLine("You took torch.");
        Assert.NotEqual(DeathRecoveryStatus.Recovered, h.Latest.Status);   // one still out
        h.Recovery.FeedTestLine("You took torch.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Stock_Recovery_YouTookOnPromptRow_StillDecrements()
    {
        using GraphHarness h = new();
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Paradigm = false;
        h.Recovery.AutoRecover = true;

        h.EnterGates();
        h.FeedSurvey("a torch");
        Assert.Contains("get torch", h.Sent);

        // The get confirmation is redrawn onto the prompt row (IsPromptLine=true) —
        // recovery must still count it (else the pile never finalises).
        h.Recovery.FeedTestLine("You took torch.", isPromptLine: true);
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Stock_Recovery_OnlyCurrencyLeft_FinalisesRecovered()
    {
        // "If currency is the only thing not recovered, treat the death as recovered."
        // Coins drop from the item survey (they recover as cash, never `get`-ed), so
        // they linger in UnrecoveredItems — a pile down to pocket change still finalises.
        using GraphHarness h = new();
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand") },
            new[] { "1500 gold" });
        h.Paradigm = false;
        h.Recovery.AutoRecover = true;

        h.EnterGates();
        h.FeedSurvey("an iron sword and 1500 gold");   // sword + coins on the floor

        Assert.Contains("get iron sword", h.Sent);
        Assert.DoesNotContain(h.Sent, s => s.Contains("gold"));   // coins never `get`-ed

        h.Recovery.FeedTestLine("You took an iron sword.");        // sword back; only coins left
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Stock_ManualReentry_SpilledItems_DoesNotFireLookSweep()
    {
        // Manual walk into a death room (walker idle → not a directed walk-to) must NOT
        // fire the adjacent-room look sweep, even with Auto-Recover on. Grab the floor,
        // hold Partial for the spilled piece — no `look <dir>` peeks.
        using GraphHarness h = new();
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.Paradigm = false;
        h.Recovery.AutoRecover = true;

        h.EnterGates();
        h.FeedSurvey("an iron sword");         // helm spilled to a neighbour, sword is here
        h.Recovery.FeedTestLine("You took an iron sword.");
        h.Sent.Clear();
        h.Heartbeat();                         // settle the grab (StockSettleTicks = 2)
        h.Heartbeat();

        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("look "));   // no sweep on a manual walk-in
    }

    [Fact]
    public void RecoverNow_Stock_SpilledItems_FiresLookSweep()
    {
        // Recover Now is a deliberate recovery — with a piece spilled to a neighbour it
        // sweeps the death-room exits, peeking each with `look <dir>`.
        using GraphHarness h = new();
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.Paradigm = false;

        h.EnterGates();                        // in the death room, Auto-Recover off
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.FeedSurvey("an iron sword");         // the re-look's survey — helm spilled
        h.Recovery.FeedTestLine("You took an iron sword.");
        h.Sent.Clear();
        h.Heartbeat();                         // settle → sweep the exits
        h.Heartbeat();

        Assert.Contains(h.Sent, s => s.StartsWith("look "));   // deliberate → look-sweep started
    }

    [Fact]
    public void Stock_PassThrough_GrabsSpilloverFromAdjacentRoom()
    {
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("steel helm", "Head") }, Array.Empty<string>());
        // Simulate a pile whose helm overflowed into the room next door.
        h.Latest.UnrecoveredItems = new List<string> { "steel helm" };
        h.Latest.Status = DeathRecoveryStatus.Partial;
        h.Paradigm = false;
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        // Walk into North Square (1/3), which borders the death room (1/1).
        h.Tracker.NoteRoomObserved(Obs3());
        h.FeedSurvey("a steel helm");                 // our overflow is on this floor
        Assert.Contains("get steel helm", h.Sent);    // grabbed in-stride, no detour

        h.Recovery.FeedTestLine("You took a steel helm.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Contains("wear steel helm", h.Sent);   // recovered gear re-worn
    }

    // North Square (1/3) — the adjacent room, used to leave and re-enter the death room.
    private static RoomObservation Obs3()
        => new("North Square", new HashSet<Direction>(new[] { Direction.S }));

    // ----- Stock: what is never on a floor ----------------------------

    [Fact]
    public void Stock_ItemReturnedToItsRightfulPlace_IsNotWaitedFor()
    {
        // The line prints as the death happens, before the lives readout that makes
        // the record, and names an item that is gone for good.
        using GraphHarness h = new() { Paradigm = false };
        h.EnterGates();
        h.Snapshot = SnapWith(
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.Recovery.FeedTestLine("You have been killed!");
        h.Recovery.FeedTestLine("Your steel helm has returned to its rightful place.");
        h.Tracker.NoteDeath(2, "You have 2 lives left.");
        h.Recovery.AutoRecover = true;

        Assert.Equal(new[] { "steel helm" }, h.Latest.ReturnedItems);

        h.EnterGates();
        h.FeedSurvey("an iron sword");
        Assert.DoesNotContain("get steel helm", h.Sent);
        h.Recovery.FeedTestLine("You took an iron sword.");

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);   // the helm isn't waited for
    }

    [Fact]
    public void Paradigm_ReturnedLine_ChangesNothing()
    {
        using GraphHarness h = new();   // Paradigm
        h.EnterGates();
        h.Snapshot = SnapWith(new[] { new EquippedItem("steel helm", "Head") }, Array.Empty<string>());
        h.Recovery.FeedTestLine("Your steel helm has returned to its rightful place.");
        h.Tracker.NoteDeath(2, "You have 2 lives left.");

        Assert.Null(h.Latest.ReturnedItems);
        Assert.Null(h.Latest.Trail);
        h.EnterGates();
        Assert.Equal(new[] { "steel helm" }, h.Latest.UnrecoveredItems);
    }

    [Fact]
    public void Stock_ItemThatStaysOnTheCharacter_IsNeverMissing_AndIsWornAgain()
    {
        // A Loyal or CursedMajor item isn't dropped (the probe reads its abilities),
        // but a death takes everything off, so it goes back on with the rest.
        using GraphHarness h = new() { Paradigm = false };
        h.Stays.Add("signet ring");
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("signet ring", "Finger") },
            Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        h.EnterGates();
        Assert.Equal(new[] { "iron sword" }, h.Latest.UnrecoveredItems);
        h.FeedSurvey("an iron sword and a signet ring");   // someone else's ring on the floor
        Assert.DoesNotContain("get signet ring", h.Sent);
        h.Recovery.FeedTestLine("You took an iron sword.");

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Contains("wear signet ring", h.Sent);
    }

    [Fact]
    public void Stock_ReEnteringAPartlyRecoveredPile_KeepsWhatWasCountedDown()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.EnterGates();
        h.FeedSurvey("an iron sword");
        h.Recovery.FeedTestLine("You took an iron sword.");
        Assert.Equal(new[] { "steel helm" }, h.Latest.UnrecoveredItems);

        h.Tracker.NoteRoomObserved(Obs3());   // leave
        h.EnterGates();                       // and come back: the sword is in the pack, not missing

        Assert.Equal(new[] { "steel helm" }, h.Latest.UnrecoveredItems);
    }

    [Fact]
    public void Stock_WalkIn_ReadsTheSurveyThatPrintedBeforeTheRoomConfirmed()
    {
        // A room prints its floor before the exits line that confirms the move, so on
        // a walk-in the survey is already read when the grab is armed.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("a torch");
        h.EnterGates();

        Assert.Contains("get torch", h.Sent);
    }

    // ----- Stock spill sweep ------------------------------------------

    // A room filter that only avoids rooms: the walker won't plan into one.
    private sealed class AvoidFilter : IRoomFilter
    {
        public HashSet<RoomKey> Avoided { get; } = new();
        public bool IsAvoided(RoomKey key) => Avoided.Contains(key);
    }

    //        4
    //        │
    //        2
    //        │
    //        1 ── 3        1 is the death room.
    //
    // The engine's spill order from 1 is 2, 4, 3: north and on from there first, and
    // 3 only once the walk has come back for the east exit.
    private const string CrossJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Crossing", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/3", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Lane", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Yard", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Dead End", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The death room with ten ways out, each to a room with one more beyond it: twenty
    // rooms within reach of a spill, more than one sweep walks to.
    private static string StarJson()
    {
        string[] cols = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "U", "D" };
        string[] back = { "S", "N", "W", "E", "SW", "SE", "NW", "NE", "D", "U" };
        string Row(int room, Dictionary<string, string> exits) =>
            $"{{ \"Map Number\": 1, \"Room Number\": {room}, \"Name\": \"Room {room}\", \"Light\": 0, \"Shop\": 0, "
            + "\"Lair\": \"\", \"Delay\": 0, "
            + string.Join(", ", cols.Select(c => $"\"{c}\": \"{exits.GetValueOrDefault(c, "0")}\"")) + " }";
        List<string> rows = new() { Row(1, cols.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => $"1/{11 + x.i}")) };
        for (int i = 0; i < 10; i++)
        {
            rows.Add(Row(11 + i, new() { [back[i]] = "1/1", [cols[i]] = $"1/{31 + i}" }));
            rows.Add(Row(31 + i, new() { [back[i]] = $"1/{11 + i}" }));
        }
        return "[" + string.Join(",\n", rows) + "]";
    }

    // Plays the game's side of a Stock recovery over the harness and the real walker:
    // each command the client sends is answered as the game would answer it. A move
    // shows the room it leads to (floor survey first, then the exits line that
    // confirms it), a `look <dir>` shows the neighbour's floor, a `get` of something
    // on the floor confirms it, and a `sea` moves the room's hidden items into view.
    private sealed class SpillWorld : IDisposable
    {
        private static readonly Dictionary<string, Direction> Moves = new()
        {
            ["n"] = Direction.N, ["s"] = Direction.S, ["e"] = Direction.E, ["w"] = Direction.W,
            ["ne"] = Direction.NE, ["nw"] = Direction.NW, ["se"] = Direction.SE, ["sw"] = Direction.SW,
            ["u"] = Direction.U, ["d"] = Direction.D,
        };

        private readonly Dictionary<RoomKey, List<string>> _floor = new();
        private readonly Dictionary<RoomKey, List<string>> _hidden = new();
        private int _answered;

        public GraphHarness H { get; }
        public RoomKey Here { get; private set; }
        // A look shows nothing, as through a closed door or past a hidden exit.
        public bool DoorsClosed { get; set; }
        // Rooms the game refuses every move into.
        public HashSet<int> Barred { get; } = new();
        // Every room walked into by a move the client sent, in order.
        public List<int> Walked { get; } = new();

        public SpillWorld(string graphJson)
        {
            H = new GraphHarness(graphJson: graphJson, withWalker: true) { Paradigm = false };
        }

        public void Put(int room, params string[] items) => _floor[new RoomKey(1, room)] = items.ToList();
        public void Hide(int room, params string[] items) => _hidden[new RoomKey(1, room)] = items.ToList();

        // Stand in a room without a command having been sent (the walk before the
        // death, and being back in the death room for Recover Now).
        public void Enter(int room)
        {
            Here = new RoomKey(1, room);
            ShowRoom();
        }

        public void Die(EquippedItem[] worn, string[] carried)
        {
            H.Snapshot = SnapWith(worn, carried);
            H.Tracker.NoteDeath(2, "You have 2 lives left.");
        }

        // Recover Now from inside the death room, then the given number of heartbeats.
        public void RecoverNow(int beats)
        {
            _answered = H.Sent.Count;
            Assert.True(H.Recovery.RecoverNow(H.Latest));
            Run(beats);
        }

        public void Run(int beats)
        {
            for (int i = 0; i < beats; i++)
            {
                Answer();
                H.Heartbeat();
                Answer();
            }
        }

        // Commands sent since the given mark, for asserting on one stretch of a run.
        public IEnumerable<string> SentSince(int mark) => H.Sent.Skip(mark);

        private void Answer()
        {
            while (_answered < H.Sent.Count)
            {
                string cmd = H.Sent[_answered++];
                if (Moves.TryGetValue(cmd, out Direction dir))
                {
                    RoomKey target = H.Graph.GetRoom(Here)!.Exits[dir].Target;
                    if (Barred.Contains(target.Room))
                    {
                        H.Tracker.NoteMoveBlocked();
                        continue;
                    }
                    Here = target;
                    Walked.Add(Here.Room);
                    ShowRoom();
                }
                else if (cmd == "look")
                {
                    Survey(Here);
                }
                else if (cmd.StartsWith("look ", StringComparison.Ordinal)
                    && DirectionExtensions.TryFromLongName(cmd[5..], out Direction peek))
                {
                    if (!DoorsClosed) Survey(H.Graph.GetRoom(Here)!.Exits[peek].Target);
                }
                else if (cmd.StartsWith("get ", StringComparison.Ordinal))
                {
                    if (_floor.TryGetValue(Here, out List<string>? floor) && floor.Remove(cmd[4..]))
                        H.Recovery.FeedTestLine($"You took {cmd[4..]}.");
                }
                else if (cmd == "sea" && _hidden.Remove(Here, out List<string>? revealed))
                {
                    if (!_floor.TryGetValue(Here, out List<string>? floor)) _floor[Here] = floor = new List<string>();
                    floor.AddRange(revealed);
                    Survey(Here);
                }
            }
        }

        private void ShowRoom()
        {
            Room room = H.Graph.GetRoom(Here)!;
            Survey(Here);
            H.Tracker.NoteRoomObserved(new RoomObservation(room.Name, new HashSet<Direction>(room.Exits.Keys)));
        }

        private void Survey(RoomKey room)
        {
            if (_floor.TryGetValue(room, out List<string>? items) && items.Count > 0)
                H.FeedSurvey(string.Join(", ", items));
        }

        public void Dispose() => H.Dispose();
    }

    private static EquippedItem[] Worn(params string[] names) =>
        names.Select((n, i) => new EquippedItem(n, i == 0 ? "Weapon Hand" : "Head")).ToArray();

    [Fact]
    public void Sweep_WalksTheEnginesOrder_GetsWhatItFinds_AndComesBack()
    {
        // Every way out of the death room is shut, so the looks show nothing and the
        // order is the engine's alone. The walker opens what it has to on the way.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword", "steel helm"), new[] { "torch", "rope" });
        w.Put(1, "iron sword");
        w.Put(2, "steel helm");
        w.Put(4, "torch");
        w.Put(3, "rope");

        w.Enter(1);
        w.RecoverNow(beats: 40);

        // North and on from there before the east exit is come back for.
        Assert.Equal(new[] { 2, 4, 2, 1, 3, 1 }, w.Walked.ToArray());
        Assert.Contains("get steel helm", w.H.Sent);
        Assert.Contains("get torch", w.H.Sent);
        Assert.Contains("get rope", w.H.Sent);
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
        Assert.Equal(new RoomKey(1, 1), w.Here);   // back where recovery started
    }

    [Fact]
    public void Sweep_PeeksTheDeathRoomsExitsInTheEnginesOrder_BeforeItWalks()
    {
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Put(3, "rope");

        w.Enter(1);
        w.RecoverNow(beats: 40);

        List<string> looks = w.H.Sent.Where(s => s.StartsWith("look ")).ToList();
        Assert.Equal(new[] { "look north", "look east" }, looks.ToArray());
        // The look east showed the rope, so that room is walked to first, not north.
        Assert.Equal(new[] { 3, 1 }, w.Walked.ToArray());
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_StopsAsSoonAsNothingIsMissing()
    {
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword", "steel helm"), Array.Empty<string>());
        w.Put(1, "iron sword");
        w.Put(2, "steel helm");

        w.Enter(1);
        w.RecoverNow(beats: 40);

        Assert.Equal(new[] { 2, 1 }, w.Walked.ToArray());   // never on to 4 or 3
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_RoomTheWalkerCannotReach_IsSkipped()
    {
        using SpillWorld w = new(CrossJson);
        w.H.Filter.Avoided.Add(new RoomKey(1, 2));   // and 4 lies beyond it
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");

        w.Enter(1);
        w.RecoverNow(beats: 40);

        Assert.Equal(new[] { 3, 1 }, w.Walked.ToArray());
        Assert.Equal(DeathRecoveryStatus.Partial, w.H.Latest.Status);
        Assert.Equal(new[] { "rope" }, w.H.Latest.UnrecoveredItems);
        Assert.Contains("every room in the plan was tried", w.H.Latest.RecoveryMessage);
    }

    [Fact]
    public void Sweep_RoomTheGameWontLetUsInto_IsGivenUpOn_AndTheRestGoesOn()
    {
        // The walker plans a route to 2 and the game refuses the step (a door it
        // can't open, a trap it won't cross). That stop and the one beyond it are
        // given up on, and the sweep goes on to the next.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Barred.Add(2);
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Put(3, "rope");

        w.Enter(1);
        w.RecoverNow(beats: 120);

        Assert.Equal(new[] { 3, 1 }, w.Walked.ToArray());
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_WithItemsStillMissing_SearchesTheTrail_DeathRoomFirst()
    {
        // When no floor nearby had room the engine hides the item in a room walked
        // before the death, starting with the death room itself. Only a search shows
        // it, and each item is found on its own roll, so a room is searched twice.
        using SpillWorld w = new(CrossJson);
        w.Enter(3);
        w.Enter(1);                                    // the trail: 1, then 3
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Hide(3, "rope");

        Assert.Equal(new[] { 1, 3 }, w.H.Latest.Trail!.Select(r => r.Room).ToArray());

        w.Enter(1);
        w.RecoverNow(beats: 80);

        // The three spill rooms, back to the death room to search it, then the trail.
        Assert.Equal(new[] { 2, 4, 2, 1, 3, 1, 3, 1 }, w.Walked.ToArray());
        Assert.Equal(3, w.H.Sent.Count(s => s == "sea"));   // twice in 1, once in 3 (found)
        Assert.Contains("get rope", w.H.Sent);
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_NeverSearchesAStashRoom()
    {
        using SpillWorld w = new(CrossJson);
        w.H.StashRooms.Add(new RoomKey(1, 3));
        w.Enter(3);
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Hide(3, "rope");

        w.Enter(1);
        w.RecoverNow(beats: 80);

        Assert.Equal(2, w.H.Sent.Count(s => s == "sea"));   // the death room only
        Assert.DoesNotContain("get rope", w.H.Sent);
        Assert.Equal(DeathRecoveryStatus.Partial, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_WalksToTwelveRoomsAtMost_ThenComesBack()
    {
        using SpillWorld w = new(StarJson());
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });   // the rope is nowhere

        w.Enter(1);
        w.RecoverNow(beats: 200);

        // The engine's order here: north and the room beyond it, then (walking back
        // through the full death room) each of its other nine exits, and only then
        // the rooms beyond those. Twelve stops is as far as one sweep goes.
        Assert.Equal(new[] { 11, 31, 12, 13, 14, 15, 16, 17, 18, 19, 20, 32 },
            w.Walked.Where(r => r != 1).Distinct().ToArray());
        Assert.Contains("the limit", w.H.Latest.RecoveryMessage);
        Assert.Equal(DeathRecoveryStatus.Partial, w.H.Latest.Status);
        Assert.Equal(new RoomKey(1, 1), w.Here);
    }

    [Fact]
    public void Sweep_EmptyDeathRoomFloor_StillStarts()
    {
        // An empty floor prints no survey; the grab armed for one would never settle.
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword"), Array.Empty<string>());
        w.Put(2, "iron sword");

        w.Enter(1);
        w.RecoverNow(beats: 40);

        Assert.Equal(new[] { 2, 1 }, w.Walked.ToArray());
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_WaitsForAutoAll()
    {
        using SpillWorld w = new(CrossJson);
        bool autoOn = false;
        w.H.Recovery.SetAutoEnabledProbe(() => autoOn);
        w.Enter(1);
        w.Die(Worn("iron sword", "steel helm"), Array.Empty<string>());
        w.Put(1, "iron sword");
        w.Put(2, "steel helm");

        w.Enter(1);
        w.RecoverNow(beats: 20);
        Assert.DoesNotContain(w.H.Sent, s => s.StartsWith("look "));
        Assert.Empty(w.Walked);

        autoOn = true;
        w.Run(40);
        Assert.Equal(new[] { 2, 1 }, w.Walked.ToArray());
    }

    [Fact]
    public void Sweep_WaitsOutAHostileInTheDeathRoom()
    {
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword", "steel helm"), Array.Empty<string>());
        w.Put(1, "iron sword");
        w.Put(2, "steel helm");
        w.H.Hostiles = true;

        w.Enter(1);
        w.RecoverNow(beats: 20);
        Assert.Empty(w.Walked);

        w.H.Hostiles = false;
        w.Run(40);
        Assert.Equal(new[] { 2, 1 }, w.Walked.ToArray());
    }

    [Fact]
    public void Sweep_AWalkTheUserStops_EndsTheSweep_AndStartsNoOther()
    {
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.H.Coordinator.AssertGate(MovementCoordinator.UserGate);   // the first leg is planned but held
        w.H.Held = true;

        w.Enter(1);
        w.RecoverNow(beats: 20);
        Assert.Contains("walking to 1/2", w.H.Recovery.SpillSweepState);

        w.H.Walker!.Stop();
        w.H.Coordinator.ClearGate(MovementCoordinator.UserGate);
        w.H.Held = false;
        w.Run(40);

        Assert.Empty(w.Walked);
        Assert.Equal(WalkState.Idle, w.H.Walker.State);
        Assert.Contains("stopped", w.H.Latest.RecoveryMessage);
    }

    [Fact]
    public void Sweep_StopPressedWhileStandingAtAStop_StartsNoFurtherLeg()
    {
        // Between two legs the walker is idle and has no Stopped to raise, so the
        // user's Stop is handed to the sweep itself.
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");

        w.Enter(1);
        w.RecoverNow(beats: 0);
        while (w.Walked.Count == 0) w.Run(1);   // standing in the first spill room
        Assert.Contains("getting items at 1/2", w.H.Recovery.SpillSweepState);

        w.H.Recovery.StopSpillSweep("stopped by the user");
        w.Run(40);

        Assert.Equal(new[] { 2 }, w.Walked.ToArray());
        Assert.Contains("stopped by the user", w.H.Latest.RecoveryMessage);
    }

    [Fact]
    public void Sweep_HeldLeg_IsNotAStall_ButTheTimeRunsOut()
    {
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.H.Coordinator.AssertGate(MovementCoordinator.UserGate);
        w.H.Held = true;

        w.Enter(1);
        w.RecoverNow(beats: 100);   // far past the 20 s a stalled leg is given
        Assert.Equal(DeathRecoveryStatus.Partial, w.H.Latest.Status);
        Assert.Contains("walking to 1/2", w.H.Recovery.SpillSweepState);

        w.Run(600);
        Assert.Contains("out of time", w.H.Latest.RecoveryMessage);
    }

    [Fact]
    public void Sweep_DyingOnTheWay_DropsIt()
    {
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.H.Coordinator.AssertGate(MovementCoordinator.UserGate);

        w.Enter(1);
        w.RecoverNow(beats: 10);
        w.H.Tracker.NoteDeath(1, "You have 1 lives left.");
        w.H.Walker!.Stop("player died");   // as the app's death halt does
        w.H.Coordinator.ClearGate(MovementCoordinator.UserGate);
        int mark = w.H.Sent.Count;
        w.Run(40);

        Assert.Contains("died during the sweep", w.H.Recovery.SpillSweepState);
        Assert.DoesNotContain(w.SentSince(mark), s => s.StartsWith("look ") || s == "sea");
    }

    [Fact]
    public void Paradigm_NeverSweeps()
    {
        using SpillWorld w = new(CrossJson);
        w.H.Paradigm = true;
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });

        w.Enter(1);
        w.RecoverNow(beats: 40);

        Assert.Empty(w.Walked);
        Assert.DoesNotContain(w.H.Sent, s => s.StartsWith("look ") || s == "sea");
        Assert.Equal("idle", w.H.Recovery.SpillSweepState);
    }
}
