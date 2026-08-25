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

        public GraphHarness(string characterName = "Ermias")
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-deathrec-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");

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

    // North Square (1/3) — the adjacent room, used to leave and re-enter the death room.
    private static RoomObservation Obs3()
        => new("North Square", new HashSet<Direction>(new[] { Direction.S }));
}
