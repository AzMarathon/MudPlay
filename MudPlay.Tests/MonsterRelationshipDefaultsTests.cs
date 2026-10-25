using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// A monster the shipped seed leaves without a relationship takes one from its
// Monsters-table Align (user, 2026-10-25): any good a Friend, Neutral a Neutral,
// any evil an Enemy; a missing or unknown Align stays Enemy. A seed entry applies
// only to the monster of its own name. The set and the seed are written to the
// scratch data root and loaded as the client loads them, on the set switch.
public sealed class MonsterRelationshipDefaultsTests : IDisposable
{
    private const int WhiteStag = 9001, Unicorn = 9002, Hermit = 9003, Bandit = 9004, ChaosImp = 9005,
        SeededGuard = 9006, Spellbreaker = 9007, Faceless = 9008, OddThing = 9009, Sprite = 9010;

    private readonly string _set = "relation-defaults-" + Path.GetRandomFileName();
    private readonly string _bbs = "relation-defaults-" + Path.GetRandomFileName();
    private readonly string _seedFile = AppPaths.MonsterOverlaySeedFile("paradigm");

    public MonsterRelationshipDefaultsTests()
    {
        ScratchRoot.Require();
        string dir = Path.Combine(AppPaths.GameDataRoot, _set);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.json"), """[ { "Legit": 2 } ]""");
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), MonstersJson(whiteStagAlign: 0));
        Directory.CreateDirectory(Path.GetDirectoryName(_seedFile)!);
        File.WriteAllText(_seedFile, SeedJson);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.Combine(AppPaths.GameDataRoot, _set), recursive: true); }
        catch { /* best-effort */ }
        try { Directory.Delete(AppPaths.BbsFolder(_bbs), recursive: true); }
        catch { /* best-effort */ }
        try { File.Delete(_seedFile); }
        catch { /* best-effort */ }
    }

    // One monster of each Align, a seeded one whose seed says otherwise, one whose
    // number the seed gives to another monster, one seeded with a location and no
    // relationship, and two with no Align the rule knows.
    private static string MonstersJson(int whiteStagAlign) => $$"""
        [
          { "Number": 9001, "Name": "white stag", "Align": {{whiteStagAlign}}, "In Game": 1 },
          { "Number": 9002, "Name": "unicorn", "Align": 4, "In Game": 1 },
          { "Number": 9003, "Name": "hermit", "Align": 3, "In Game": 1 },
          { "Number": 9004, "Name": "bandit", "Align": 5, "In Game": 1 },
          { "Number": 9005, "Name": "chaos imp", "Align": 2, "In Game": 1 },
          { "Number": 9006, "Name": "seeded guard", "Align": 1, "In Game": 1 },
          { "Number": 9007, "Name": "amethyst spellbreaker", "Align": 3, "In Game": 1 },
          { "Number": 9008, "Name": "faceless thing", "In Game": 1 },
          { "Number": 9009, "Name": "odd thing", "Align": 9, "In Game": 1 },
          { "Number": 9010, "Name": "glade sprite", "Align": 0, "In Game": 1 }
        ]
        """;

    private const string SeedJson = """
        [
          { "Number": 9006, "Name": "Seeded Guard ", "Relationship": "Friend" },
          { "Number": 9007, "Name": "elite amethyst guardian", "Relationship": "Enemy", "Priority": "High", "Region": "Crystal Caves" },
          { "Number": 9010, "Name": "glade sprite", "Region": "Glade" }
        ]
        """;

    private sealed class Rig : IDisposable
    {
        public required MessageRouter Router { get; init; }
        public required GameDataCache Cache { get; init; }
        public required SettingsResolver Resolver { get; init; }
        public required MonsterOverlaySeedStore Seed { get; init; }
        public required LogService Log { get; init; }
        public required RoomEntityClassifier Classifier { get; init; }
        public required CombatManager Combat { get; init; }
        public required MonsterRelationshipRules Rules { get; init; }
        // What the combat engine sent the game.
        public List<string> Attacks { get; } = new();

        // What every engine that reads a relationship resolves (AppServices.ResolveMonsterOverlay).
        public MonsterRelationship? Relationship(int number) => Seed.Resolve(Resolver, number).Relationship;

        public void Room(string list) => new ServerFeed(Router).Line($"Also here: {list}.");

        public void Dispose()
        {
            Combat.Dispose();
            Classifier.Dispose();
        }
    }

    private Rig Build()
    {
        GameDataCache cache = new(AppPaths.GameDataRoot);
        LogService log = new() { Diagnostics = new LogDiagnosticState { DebugDiagnostics = true } };
        MonsterOverlaySeedStore seed = new(cache, log);
        cache.ActiveSetChanged += seed.Load;   // as AppServices wires it
        cache.SwitchSet(_set);

        ProfileService profile = TestProfiles.Service();
        BbsProfileStore boards = new();
        boards.Save(new BbsProfile { Name = _bbs, Host = "h", Realms = { new RealmProfile { Name = "home" } } });
        profile.CreateProfile(_bbs, "Tester");
        profile.Load(_bbs, "Tester");
        SettingsResolver resolver = new(new SettingsService(), boards, profile, () => cache.ActiveSet);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        MonsterMessageStore monsters = new();
        foreach ((int number, string name) in new[]
                 { (WhiteStag, "white stag"), (Unicorn, "unicorn"), (Hermit, "hermit"), (Bandit, "bandit"),
                   (ChaosImp, "chaos imp"), (Faceless, "faceless thing") })
            monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}", Name: name, Links: new[] { new GameDataLink("Monsters", number) }));
        RoomEntityClassifier classifier = new(router, monsters, new PlayerDatabase(new ProfileService(), () => null), log);
        CombatManager combat = new(router, classifier, monsters,
            resolveOverlay: n => seed.Resolve(resolver, n),
            party: new PartyState(),
            readSettings: () => new CombatSettings { NormalAttackCommand = "a" },
            isEnabled: () => true,
            readOwnGivenName: () => "Tester",
            post: a => a(),
            log: log);
        combat.SetWeaponActuator((_, _, _) => { });

        Rig rig = new()
        {
            Router = router, Cache = cache, Resolver = resolver, Seed = seed, Log = log,
            Classifier = classifier, Combat = combat,
            Rules = new MonsterRelationshipRules(resolver, new MonsterCatalog(cache), seed),
        };
        combat.SetWireSender(b =>
        {
            string line = Encoding.Latin1.GetString(b).TrimEnd('\r');
            if (line.Length > 0) rig.Attacks.Add(line);   // not the bare CR room refresh
        });
        return rig;
    }

    // ----- The rule --------------------------------------------------------------

    [Theory]
    [InlineData(WhiteStag, MonsterRelationship.Friend)]     // Good
    [InlineData(Unicorn, MonsterRelationship.Friend)]       // Lawful Good
    [InlineData(Hermit, MonsterRelationship.Neutral)]       // Neutral
    [InlineData(Bandit, MonsterRelationship.Enemy)]         // Neutral Evil
    [InlineData(ChaosImp, MonsterRelationship.Enemy)]       // Chaotic Evil
    public void AnUnseededMonster_TakesItsRelationshipFromItsAlignment(int number, MonsterRelationship expected)
    {
        using Rig r = Build();

        Assert.Equal(expected, r.Relationship(number));
        Assert.Equal(expected, r.Rules.Read(number));
        Assert.StartsWith("its alignment, ", r.Seed.RelationshipSourceOf(r.Resolver, number));
    }

    [Theory]
    [InlineData(Faceless)]
    [InlineData(OddThing)]
    public void AMissingOrUnknownAlign_StaysEnemy(int number)
    {
        using Rig r = Build();

        Assert.Null(r.Relationship(number));
        Assert.Equal(MonsterRelationship.Enemy, r.Rules.Read(number));
        Assert.Equal("no seed or alignment", r.Seed.RelationshipSourceOf(r.Resolver, number));
    }

    [Fact]
    public void ASeededRelationship_WinsOverTheAlignment_ItsNameMatchedLooselyByCaseAndSpace()
    {
        using Rig r = Build();

        Assert.Equal(MonsterRelationship.Friend, r.Relationship(SeededGuard));   // Evil by Align
        Assert.Equal("the seed", r.Seed.RelationshipSourceOf(r.Resolver, SeededGuard));
    }

    [Fact]
    public void ASeedEntryWithAnotherMonstersName_IsLeftOutWhole_AndTheAlignmentApplies()
    {
        using Rig r = Build();

        MonsterOverlay overlay = r.Seed.GetOverlay(Spellbreaker);
        Assert.Equal(MonsterRelationship.Neutral, overlay.Relationship);
        Assert.Null(overlay.Priority);
        Assert.Null(overlay.Region);
        Assert.DoesNotContain("Crystal Caves", r.Seed.LocationSuggestions.Regions);
    }

    [Fact]
    public void ASeedEntryWithNoRelationship_KeepsItsLocation_AndTakesTheAlignmentsRelationship()
    {
        using Rig r = Build();

        MonsterOverlay overlay = r.Seed.GetOverlay(Sprite);
        Assert.Equal(MonsterRelationship.Friend, overlay.Relationship);
        Assert.Equal("Glade", overlay.Region);
        Assert.Contains("Glade", r.Seed.LocationSuggestions.Regions);
    }

    [Fact]
    public void AnOverrideTier_StillWins_AndIsNamedAsTheSource()
    {
        using Rig r = Build();
        r.Resolver.WriteGameDataAt(SettingsTier.Character, "Monsters", WhiteStag.ToString(),
            new Dictionary<string, object> { ["Relationship"] = "Enemy" });

        Assert.Equal(MonsterRelationship.Enemy, r.Relationship(WhiteStag));
        Assert.Equal("the Character tier", r.Seed.RelationshipSourceOf(r.Resolver, WhiteStag));
    }

    // ----- What reads it -----------------------------------------------------------

    [Fact]
    public void AGoodMonster_IsNotAttacked_AnEvilOneIs()
    {
        using Rig r = Build();

        r.Room("white stag, unicorn, hermit");
        Assert.Empty(r.Attacks);

        r.Room("white stag, bandit");
        Assert.Equal(new[] { "a bandit" }, r.Attacks);
    }

    [Fact]
    public void SettingTheAlignmentsRelationship_LeavesNoOverride_AsForTheSeedsOwn()
    {
        using Rig r = Build();

        Assert.Equal(MonsterRelationship.Friend, r.Rules.Change(WhiteStag, MonsterRelationship.Friend));
        Assert.Equal(SettingsTier.Defaults, r.Resolver.GetGameDataSourceTier("Monsters", WhiteStag.ToString()));
    }

    [Fact]
    public void TheMonsterEditor_ShowsTheAlignmentsRelationship_AsTheInstalledDefault()
    {
        using Rig r = Build();

        // Built as MonsterRecordDialogService.OpenAsync builds it; the window itself
        // needs the running app.
        MonsterEditDialogViewModel vm = new(
            wccNoStr: WhiteStag.ToString(), mdbName: "white stag", existing: r.Seed.Resolve(r.Resolver, WhiteStag),
            currentTier: SettingsTier.Defaults, mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: r.Resolver.WritableTiers(), installedDefaults: r.Seed.GetOverlay(WhiteStag));

        Assert.Equal(MonsterRelationship.Friend, vm.Relationship);
    }

    // ----- Program log and the rankings' stamp -------------------------------------

    [Fact]
    public void TheLoad_IsLoggedOnce_WithItsCounts_AndTheEntriesLeftOut()
    {
        using Rig r = Build();

        LogEntry info = Assert.Single(r.Log.Snapshot(), e => e.Severity == LogSeverity.Info
            && e.Message.StartsWith("Monster relationships for", StringComparison.Ordinal));
        Assert.Contains("1 from the seed, 7 from alignment (3 friend / 2 neutral / 2 enemy), "
            + "2 with no alignment (Enemy), 1 seed entries skipped (name differs)", info.Message);
        Assert.Contains(r.Log.Snapshot(), e => e.Severity == LogSeverity.Debug
            && e.Message.Contains("#9007 seed 'elite amethyst guardian', set 'amethyst spellbreaker'"));
        Assert.Contains("7 from alignment", r.Seed.RelationshipSummary);
    }

    // A re-import that changes a monster's alignment changes which monsters are
    // fought, so a kept Simulator ranking is no longer of this world.
    [Fact]
    public void AnAlignmentChange_IsReadOnReimport_AndMovesTheStamp()
    {
        using Rig r = Build();
        string before = r.Seed.ContentStamp;

        File.WriteAllText(Path.Combine(AppPaths.GameDataRoot, _set, "Monsters.json"), MonstersJson(whiteStagAlign: 1));
        r.Cache.ReloadActiveSet();

        Assert.Equal(MonsterRelationship.Enemy, r.Relationship(WhiteStag));
        Assert.NotEqual(before, r.Seed.ContentStamp);
    }
}
