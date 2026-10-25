using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Health;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// @relation <monster> [relationship]: a player with Alter settings reports, or sets
// for this character, how the client treats one monster (user, 2026-10-25). The
// change is saved at the character tier, the Relationship field alone, and the
// engines that read a relationship act on it at once.
public sealed class MonsterRelationshipRemoteTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
    private const string Friend = "Friend";
    private const string Stranger = "Stranger";
    private const int DaoLord = 597;

    private readonly string _root;
    private readonly string _set = "relation-remote-" + Path.GetRandomFileName();
    private readonly string _bbs = "relation-remote-" + Path.GetRandomFileName();

    public MonsterRelationshipRemoteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-relation-remote-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, _set));
        File.WriteAllText(Path.Combine(_root, _set, "Monsters.json"), MonstersJson);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
        try { Directory.Delete(AppPaths.BbsFolder(_bbs), recursive: true); }
        catch { /* best-effort */ }
        try { File.Delete(AppPaths.OverrideFile(SettingsTier.Global, null, "Monsters", _set)); }
        catch { /* best-effort */ }
    }

    // Two "orc" records (a variant per area) and an "orc chief"; a "hill giant" in play
    // and an out-of-play one of the same name; a "frost giant" whose name holds an
    // apostrophe-free word pair.
    private const string MonstersJson = """
        [
          { "Number": 597, "Name": "dao lord", "In Game": 1 },
          { "Number": 12, "Name": "orc", "In Game": 1 },
          { "Number": 13, "Name": "orc", "In Game": 1 },
          { "Number": 40, "Name": "orc chief", "In Game": 1 },
          { "Number": 50, "Name": "hill giant", "In Game": 1 },
          { "Number": 51, "Name": "hill giant", "In Game": 0 },
          { "Number": 70, "Name": "kobold's pet", "In Game": 1 },
          { "Number": 80, "Name": "cave bear", "In Game": 0 }
        ]
        """;

    private sealed class Rig : IDisposable
    {
        public required MessageRouter Router { get; init; }
        public required RemoteCommandManager Engine { get; init; }
        public required SettingsResolver Resolver { get; init; }
        public required AutoModeController Controller { get; init; }
        public required MonsterRelationshipHandler Handler { get; init; }
        public required MonsterOverlaySeedStore Seed { get; init; }
        public required LogService Log { get; init; }
        public required RoomEntityClassifier Classifier { get; init; }
        public required MonsterRelationshipWatcher Watcher { get; init; }
        // What the watch answered: HealthManager's hang-up and flee, as it is handed them.
        public required List<string> HangUps { get; init; }
        public required List<string> Flees { get; init; }
        public List<byte[]> Replies { get; } = new();

        public List<string> Sent => Replies.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public void Ask(string message, string sender = Friend, ChatChannel channel = ChatChannel.TelepathIncoming) =>
            ChatLines.Hear(Router, new ChatLine(channel, sender, message));

        public string Ask1(string message)
        {
            Replies.Clear();
            Ask(message);
            return Assert.Single(Sent);
        }

        // What every engine that reads a relationship resolves (AppServices.ResolveMonsterOverlay).
        public MonsterOverlay Overlay(int number) =>
            Resolver.ResolveGameData("Monsters", number.ToString(), Seed.GetOverlay(number));

        public SettingsTier TierOf(int number) => Resolver.GetGameDataSourceTier("Monsters", number.ToString());

        public void Room(string list) => new ServerFeed(Router).Line($"Also here: {list}.");

        public void Dispose()
        {
            Watcher.Dispose();
            Classifier.Dispose();
            Handler.Dispose();
            Engine.Dispose();
        }
    }

    private Rig Build(bool named = true)
    {
        GameDataCache cache = new(_root);
        cache.SwitchSet(_set);
        MonsterCatalog catalog = new(cache);

        ProfileService profile = TestProfiles.Service();
        BbsProfileStore boards = new();
        if (named)
        {
            boards.Save(new BbsProfile { Name = _bbs, Host = "h", Realms = { new RealmProfile { Name = "home" } } });
            profile.CreateProfile(_bbs, "Tester");
            profile.Load(_bbs, "Tester");
        }
        else
        {
            profile.LoadDefaultProfile();
        }
        SettingsResolver resolver = new(new SettingsService(), boards, profile, () => cache.ActiveSet);
        MonsterOverlaySeedStore seed = new();

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        players.RecordObservation(Friend, null, null, null, null, null, null, Now);
        players.EditCustomization(Friend, new PlayerCustomization(RemoteControls: PlayerRemoteControls.AlterSettings));
        players.RecordObservation(Stranger, null, null, null, null, null, null, Now);
        players.EditCustomization(Stranger, new PlayerCustomization(
            RemoteControls: PlayerRemoteControls.All & ~PlayerRemoteControls.AlterSettings));
        AutoModeController controller = new(profile);
        LogService log = new();
        RemoteCommandManager engine = new(new ChatRouter(router), party, players)
        {
            BlockedByMasterSwitch = what => controller.Blocks("Remote commands", what),
        };
        MonsterRelationshipHandler handler = new(engine, new MonsterRelationshipRules(resolver, catalog, seed), log,
            now: () => new DateTimeOffset(Now));

        // The Flee / Hangup watch, reading relationships through the resolver as the
        // client wires it.
        MonsterMessageStore monsters = new();
        foreach ((int number, string name) in new[] { (DaoLord, "dao lord"), (50, "hill giant") })
            monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}", Name: name, Links: new[] { new GameDataLink("Monsters", number) }));
        RoomEntityClassifier classifier = new(router, monsters, players, log);
        List<string> hangUps = new();
        List<string> flees = new();
        MonsterRelationshipWatcher watcher = new(
            classifier,
            resolveOverlay: n => resolver.ResolveGameData("Monsters", n.ToString(), seed.GetOverlay(n)),
            hangUp: reason => { hangUps.Add(reason); return EscapeOutcome.Jumped; },
            flee: (reason, _) => { flees.Add(reason); return FleeOutcome.Started; },
            fleeInFlight: () => true,
            masterSwitchOff: () => false,
            hangupsDisabled: () => false,
            pvpHandles: _ => false,
            atBoardMenu: () => false,
            describeRoom: () => "here",
            schedule: (_, _) => { },
            log: log);

        Rig rig = new()
        {
            Router = router, Engine = engine, Resolver = resolver, Controller = controller, Handler = handler,
            Seed = seed, Log = log, Classifier = classifier, Watcher = watcher, HangUps = hangUps, Flees = flees,
        };
        engine.SetWireSender(rig.Replies.Add);
        return rig;
    }

    private static string Reply(string text) => $"/{Friend} {{{text}}}";

    // ----- Report ------------------------------------------------------------------

    [Fact]
    public void AMonster_IsReported_AsTheEnginesReadIt_AndNothingIsWritten()
    {
        using Rig r = Build();

        Assert.Equal(Reply("dao lord (#597): Enemy"), r.Ask1("@relation dao lord"));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(DaoLord));

        r.Resolver.WriteGameDataAt(SettingsTier.Global, "Monsters", "597",
            new MonsterOverlay { Relationship = MonsterRelationship.Neutral });
        Assert.Equal(Reply("dao lord (#597): Neutral"), r.Ask1("@relation dao lord"));
    }

    // ----- Changes -----------------------------------------------------------------

    [Fact]
    public void AChange_IsSavedForThisCharacter_TheRelationshipAlone()
    {
        using Rig r = Build();

        Assert.Equal(Reply("dao lord (#597): Enemy -> Friend"), r.Ask1("@relation dao lord friend"));

        Assert.Equal(MonsterRelationship.Friend, r.Overlay(DaoLord).Relationship);
        Assert.Equal(SettingsTier.Character, r.TierOf(DaoLord));
        Assert.Equal(new[] { "Relationship" },
            r.Resolver.ReadGameDataAt(SettingsTier.Character, "Monsters", "597")!.Select(p => p.Key));
        Assert.False(File.Exists(AppPaths.OverrideFile(SettingsTier.Global, null, "Monsters", _set)));
    }

    [Theory]
    [InlineData("FLEE", MonsterRelationship.Flee)]
    [InlineData("Hangup", MonsterRelationship.Hangup)]
    [InlineData("neutral", MonsterRelationship.Neutral)]
    public void TheRelationshipWord_IsReadInAnyCase(string word, MonsterRelationship expected)
    {
        using Rig r = Build();

        Assert.Equal(Reply($"dao lord (#597): Enemy -> {expected}"), r.Ask1($"@relation dao lord {word}"));
        Assert.Equal(expected, r.Overlay(DaoLord).Relationship);
    }

    [Fact]
    public void TheSameRelationship_IsAnsweredAsAlreadySet_AndLeavesNoOverride()
    {
        using Rig r = Build();

        Assert.Equal(Reply("dao lord (#597): already Enemy"), r.Ask1("@relation dao lord enemy"));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(DaoLord));
    }

    // A record the Monster editor saved for this character keeps every other field.
    [Fact]
    public void AChange_LeavesTheRecordsOtherFieldsAsTheyWere()
    {
        using Rig r = Build();
        r.Resolver.WriteGameDataAt(SettingsTier.Character, "Monsters", "597",
            new MonsterOverlay { Relationship = MonsterRelationship.Enemy, Priority = MonsterAttackPriority.High, DontBackstab = true });

        r.Ask("@relation dao lord hangup");

        MonsterOverlay now = r.Overlay(DaoLord);
        Assert.Equal(MonsterRelationship.Hangup, now.Relationship);
        Assert.Equal(MonsterAttackPriority.High, now.Priority);
        Assert.True(now.DontBackstab);
    }

    // Held over a lower tier, and back to what lies below clears this character's field.
    [Fact]
    public void AChange_HoldsOverAGlobalRelationship_AndBackToTheDefaultClearsIt()
    {
        using Rig r = Build();
        r.Resolver.WriteGameDataAt(SettingsTier.Global, "Monsters", "597",
            new Dictionary<string, object> { ["Relationship"] = "Friend" });

        Assert.Equal(Reply("dao lord (#597): Friend -> Enemy"), r.Ask1("@relation dao lord enemy"));
        Assert.Equal(MonsterRelationship.Enemy, r.Overlay(DaoLord).Relationship);
        Assert.Equal(SettingsTier.Character, r.TierOf(DaoLord));

        r.Resolver.ClearGameDataAt(SettingsTier.Global, "Monsters", "597");
        r.Ask("@relation dao lord flee");
        Assert.Equal(Reply("dao lord (#597): Flee -> Enemy"), r.Ask1("@relation dao lord enemy"));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(DaoLord));
    }

    [Fact]
    public void AChange_IsLoggedWithTheSender_TheChannel_AndBothValues_AndKeptForTheBugReport()
    {
        using Rig r = Build();

        r.Ask("@relation dao lord hangup", channel: ChatChannel.Gangpath);

        Assert.Contains(r.Log.Snapshot(), e => e.Severity == LogSeverity.Info
            && e.Message.Contains("@relation from Friend on gangpath")
            && e.Message.Contains("dao lord (#597)")
            && e.Message.Contains("Enemy (from the Defaults tier) -> Hangup"));
        MonsterRelationshipHandler.RemoteChange change = Assert.IsType<MonsterRelationshipHandler.RemoteChange>(r.Handler.LastChange(DaoLord));
        Assert.Equal(("Friend", RemoteChannel.Gangpath, MonsterRelationship.Enemy, MonsterRelationship.Hangup),
            (change.Sender, change.Channel, change.From, change.To));
        Assert.Null(r.Handler.LastChange(50));
    }

    // ----- What reads it -----------------------------------------------------------

    [Fact]
    public void TheChange_ReachesCombatTargeting_AtOnce()
    {
        using Rig r = Build();
        Assert.True(MonsterEngagement.IsEngageable(r.Overlay(DaoLord)));

        r.Ask("@relation dao lord friend");
        Assert.False(MonsterEngagement.IsEngageable(r.Overlay(DaoLord)));

        r.Ask("@relation dao lord enemy");
        Assert.True(MonsterEngagement.IsEngageable(r.Overlay(DaoLord)));
    }

    [Fact]
    public void TheChange_ReachesTheHangupAndFleeWatch_AtTheNextRoomDisplay()
    {
        using Rig r = Build();
        r.Room("dao lord, hill giant");
        Assert.Empty(r.HangUps);
        Assert.Empty(r.Flees);

        r.Ask("@relation dao lord hangup");
        r.Room("dao lord");
        Assert.Equal("dao lord (#597) is here, relationship Hangup", Assert.Single(r.HangUps));

        r.Ask("@relation hill giant flee");
        r.Room("hill giant");
        Assert.Single(r.Flees);
    }

    [Fact]
    public void TheMonsterEditor_OpensOnTheChange()
    {
        using Rig r = Build();
        r.Ask("@relation dao lord flee");

        MonsterEditDialogViewModel vm = new(
            wccNoStr: "597", mdbName: "dao lord", existing: r.Overlay(DaoLord),
            currentTier: r.TierOf(DaoLord), mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: [SettingsTier.Character, SettingsTier.Bbs, SettingsTier.Global]);

        Assert.Equal(MonsterRelationship.Flee, vm.Relationship);
        Assert.Equal(SettingsTier.Character, vm.UseTier);
    }

    // ----- Names -------------------------------------------------------------------

    [Fact]
    public void TheWordsMayComeInAnyOrder_AndApostrophesMayBeLeftOut()
    {
        using Rig r = Build();

        Assert.Equal(Reply("dao lord (#597): Enemy -> Friend"), r.Ask1("@relation lord dao friend"));
        Assert.Equal(Reply("kobold's pet (#70): Enemy -> Friend"), r.Ask1("@relation kobolds pet friend"));
    }

    // The monster in play wins over an out-of-play one of its name, and one out of play
    // is still found when nothing in play fits.
    [Fact]
    public void TheMonstersInPlay_AreSearchedFirst()
    {
        using Rig r = Build();

        Assert.Equal(Reply("hill giant (#50): Enemy -> Neutral"), r.Ask1("@relation hill giant neutral"));
        Assert.Equal(Reply("cave bear (#80): Enemy"), r.Ask1("@relation cave bear"));
    }

    [Fact]
    public void ANumber_NamesOneOfSeveralSameNamedMonsters()
    {
        using Rig r = Build();

        Assert.Equal(Reply("orc (#13): Enemy -> Friend"), r.Ask1("@relation #13 friend"));
        Assert.Equal(MonsterRelationship.Friend, r.Overlay(13).Relationship);
        Assert.Equal(SettingsTier.Defaults, r.TierOf(12));
        Assert.Equal(Reply("hill giant (#51): Enemy"), r.Ask1("@relation #51"));
    }

    // ----- Refusals ----------------------------------------------------------------

    public static TheoryData<string, string> Refusals => new()
    {
        { "@relation balrog enemy", "no monster named 'balrog'" },
        { "@relation balrog", "no monster named 'balrog'" },
        { "@relation #999 enemy", "no monster named '#999'" },
        { "@relation orc friend", "monster name 'orc' matches 2: 'orc' #12, 'orc' #13" },
        { "@relation orc", "monster name 'orc' matches 2: 'orc' #12, 'orc' #13" },
        { "@relation dao lord frend", "bad relationship 'frend': use neutral, enemy, friend, flee or hangup" },
        { "@relation dao lord 2", "bad relationship '2': use neutral, enemy, friend, flee or hangup" },
        { "@relation", "usage: @relation <monster> [neutral|enemy|friend|flee|hangup]" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AFailure_IsRefusedWithItsReason_AndNothingIsWritten(string command, string reason)
    {
        using Rig r = Build();

        Assert.Equal(Reply(reason), r.Ask1(command));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(DaoLord));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(12));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(13));
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void WarnOff_AFailure_IsAnsweredWithNothing(string command, string reason)
    {
        _ = reason;
        using Rig r = Build();
        r.Engine.WarnOnDenial = false;

        r.Ask(command);

        Assert.Empty(r.Replies);
    }

    [Fact]
    public void WarnOff_ASuccess_IsStillAnswered()
    {
        using Rig r = Build();
        r.Engine.WarnOnDenial = false;

        Assert.Equal(Reply("dao lord (#597): Enemy -> Flee"), r.Ask1("@relation dao lord flee"));
    }

    [Fact]
    public void NoCharacterLoaded_AChangeIsRefused_AReportIsAnswered()
    {
        using Rig r = Build(named: false);

        Assert.Equal(Reply("no character loaded"), r.Ask1("@relation dao lord friend"));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(DaoLord));
        Assert.Equal(Reply("dao lord (#597): Enemy"), r.Ask1("@relation dao lord"));
    }

    [Fact]
    public void WithoutAlterSettings_TheFailureMessage_AndNothingIsWritten()
    {
        using Rig r = Build();

        r.Ask("@relation dao lord friend", Stranger);

        Assert.Equal($"/{Stranger} {{{r.Engine.FailureMessage}}}", Assert.Single(r.Sent));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(DaoLord));
    }

    [Fact]
    public void WithoutAlterSettings_WarnOff_NothingIsAnswered_AndNothingIsWritten()
    {
        using Rig r = Build();
        r.Engine.WarnOnDenial = false;

        r.Ask("@relation dao lord friend", Stranger);

        Assert.Empty(r.Replies);
        Assert.Equal(SettingsTier.Defaults, r.TierOf(DaoLord));
    }

    [Fact]
    public void TheReply_GoesBackOnTheChannelTheCommandCameIn()
    {
        using Rig r = Build();

        r.Ask("@relation dao lord friend", channel: ChatChannel.Gangpath);
        r.Ask("@relation balrog", channel: ChatChannel.Gangpath);

        Assert.Equal(new[] { "bg {dao lord (#597): Enemy -> Friend}", "bg {no monster named 'balrog'}" }, r.Sent);
    }

    [Fact]
    public void MasterSwitchOff_IsIgnored_WithNoAnswer()
    {
        using Rig r = Build();
        r.Controller.TurnOff("test");

        r.Ask("@relation dao lord friend");
        r.Ask("@relation dao lord");

        Assert.Empty(r.Replies);
        Assert.Equal(SettingsTier.Defaults, r.TierOf(DaoLord));
    }
}
