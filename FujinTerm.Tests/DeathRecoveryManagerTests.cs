using System.IO;
using System.Text;
using FujinTerm.Game.Cash;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Game.Recovery;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="DeathRecoveryManager"/>'s stock corpse-recovery flow: on
/// re-entering the death room, auto-recover reads the "You notice" survey and, if
/// our "corpse of &lt;given-name&gt;" is on the floor, sends ONE
/// <c>recover corpse &lt;name&gt;</c>; the single "You have recovered the corpse
/// of &lt;name&gt;." line finalises. When the corpse is NOT in the survey the pile
/// is marked Missing (terminal) so it never spam-retries. (The <c>@comeback</c>
/// party-pickup flow is a separate concern owned by
/// <see cref="FujinTerm.Game.Remote.PartyComebackManager"/>.)
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
            _root = Path.Combine(Path.GetTempPath(), "fujinterm-deathrec-" + Path.GetRandomFileName());
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

    // North Square (1/3) — the adjacent room, used to leave and re-enter the death room.
    private static RoomObservation Obs3()
        => new("North Square", new HashSet<Direction>(new[] { Direction.S }));
}
