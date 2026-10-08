using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A MegaMUD character file becomes a MudPlay character: settings that mean the same
// on both sides carry over under their MudPlay names, the rest are listed as left
// behind, and nothing in the file is guessed at.
public sealed class MegaMudProfileImporterTests
{
    // A cut-down character file in MegaMUD's own layout. The login is made up.
    private const string Ini = """
        [MegaMud]
        MainWinPos=7,69,739,720
        AutoCombat=1
        AutoHeal=0
        AutoCash=1
        AutoSneak=0
        FavRoom0=66314000
        [Profiles]
        ActiveProfile=2
        Name1=Smash
        Name2=Bash
        [BBS]
        BbsName=Some Board
        UserID=testuser
        Password=not-a-real-password
        [Health]
        HpFull%=86
        HpRest%=70
        HpHealAtt%=77
        HpHealMinor%=85
        HpRun%=0
        ManaBless%=40
        UseMeditate=1
        PrePostRest=1
        PreRestCmd=rem bala^Meq chal^M
        PostRestCmd=eq bala^M
        [Health.P2]
        Enabled=1
        HpFull%=90
        HpRest%=50
        [Spells]
        HealCmd=grhe
        HealCmd2=mahe
        FreedomCmd=curp
        BlessCmd1=grze
        BlessCmd2=dfav
        BlessCmd3=
        [Combat]
        AttackCmd=smash
        NrmWeapon=longsword1
        AttackSpl=
        MultAttack=stor
        ManaMultAtt%=8
        MultCastCnt=0
        MultMstrCnt=3
        MaxMstrs=99
        MinMstrs=0
        RunRooms=1
        AttMaxDmg=99999
        [Combat.P1]
        Enabled=1
        AttackCmd=smash
        [Combat.P2]
        Enabled=1
        AttackCmd=bash
        MultAttack=stor
        ManaMultAtt%=12
        [Party]
        ParPeriod=5
        PartyHeal1=mahe
        PartyHeal1%=80
        PartyHealArea=mrai
        PartyHealAreaAt=2
        PartyHealArea%=60
        PartyBless2=dfav
        PartyBlessWait2=180
        PartyBless3=prev
        PartyRank=0
        [Cash]
        WantCopper=0
        WantGold=1
        DontBeHeavy=1
        LimitWealth=1
        MaxWealth=1500000
        Bank=Bank of Godfrey-1 297
        [Talk]
        NoGangCmds=1
        CmdReply={command invalid or not allowed}
        [Other]
        BlessResting=0
        BlessCombat=1
        IgnoreBlind=1
        RunBackwards=1
        PickMax=3
        [Comms]
        RedialMax=0
        RedialPause=45
        RedialConnect=1
        RedialCarrier=1
        RedialNoResponse=0
        RedialCleanup=1
        CleanupPeriod=5000
        [PvP]
        PvpAction=5
        """;

    private static (MegaMudImportPlan Plan, CharacterProfile Made) Import(bool withLogin = true,
        Dictionary<string, string>? edits = null)
    {
        MegaMudImportPlan plan = MegaMudProfileImporter.Read(MegaMudIni.Parse(Ini), "Cleric");
        var made = new CharacterProfile();
        string keyFile = Path.Combine(Path.GetTempPath(), "mudplay-megamud-import-" + Path.GetRandomFileName());
        plan.ApplyTo(made, "Paradigm", withLogin, new PasswordProtector(keyFile), edits);
        try { File.Delete(keyFile); } catch (IOException) { /* best-effort */ }
        return (plan, made);
    }

    private static T Section<T>(CharacterProfile profile, string key) =>
        JsonSerializer.Deserialize<T>(profile.Settings![key], JsonStore.Options)!;

    [Fact]
    public void Ini_ReadsSectionsAndKeys_WithoutRegardToCase_AndKeepsTheValueWhole()
    {
        MegaMudIni ini = MegaMudIni.Parse("[Health]\r\nHpFull%=86\r\nPreRestCmd=a=b^M\r\n; not a key\r\n[Empty]\r\n");

        Assert.Equal("86", ini.Get("health", "hpfull%"));
        Assert.Equal("a=b^M", ini.Get("Health", "PreRestCmd"));
        Assert.Null(ini.Get("Health", "Missing"));
        Assert.True(ini.HasSection("empty"));
        Assert.False(ini.HasSection("Combat"));
    }

    // The profile MegaMUD had active is the one whose values go live: its own
    // section's where it has them, the values in play where it doesn't.
    [Fact]
    public void ActiveProfile_BecomesTheLiveSettings()
    {
        (_, CharacterProfile made) = Import();

        HealthSettings health = Section<HealthSettings>(made, "Health");
        Assert.Equal(90, health.RestMaxHp);              // Health.P2
        Assert.Equal(50, health.RestIfBelowHp);          // Health.P2
        Assert.Equal(77, health.MajorHealCombatTrigger); // not in P2: the value in play
        Assert.Equal(85, health.MinorHealCombatTrigger);
        Assert.Equal(0, health.RunIfBelowHp);
        Assert.True(health.UseMeditateAbility);
        Assert.Equal("rem bala^Meq chal^M", health.PreRestCommand);

        CombatSettings combat = Section<CombatSettings>(made, "Combat");
        Assert.Equal("bash", combat.NormalAttackCommand);
        Assert.Equal("stor", combat.MultiAttackSpell.SpellName);
        Assert.Equal(12, combat.MultiAttackSpell.MinManaPerCast);
        Assert.Null(combat.MultiAttackSpell.MaxCastsPerRoom);   // MegaMUD's 0 is no limit
        Assert.Equal(3, combat.MultiAttackSpell.MinEnemies);
        Assert.Equal(20, combat.MaxMonstersInRoom);             // a room holds 20
        Assert.Equal(RunDirection.Backward, combat.RunDirection);

        SpellsSettings spells = Section<SpellsSettings>(made, "Spells");
        Assert.Equal("grhe", spells.MajorHealSpell);
        Assert.Equal("mahe", spells.MinorHealSpell);
        Assert.Equal("curp", spells.CureHoldsSpell);
        Assert.True(spells.IgnoreBlindness);
    }

    [Fact]
    public void EachMegaMudProfile_BecomesACombatProfile_WithTheActiveOneActive()
    {
        (_, CharacterProfile made) = Import();

        CombatProfileSettings profiles = made.CombatProfiles!;
        Assert.Equal(new[] { "Smash", "Bash" }, profiles.Profiles.Select(p => p.Name).ToArray());
        Assert.Equal("smash", profiles.Profiles[0].NormalAttackCommand);
        Assert.Equal(86, profiles.Profiles[0].Health.RestMaxHp);   // no Health.P1: the values in play
        Assert.Equal("bash", profiles.Profiles[1].NormalAttackCommand);
        Assert.Equal(90, profiles.Profiles[1].Health.RestMaxHp);
        Assert.Equal(profiles.Profiles[1].Id, profiles.ActiveId);
        Assert.Equal("grhe", profiles.Profiles[0].Spells.MajorHealSpell);
    }

    [Fact]
    public void Blesses_BecomeBuffSlots_SelfAndParty()
    {
        (_, CharacterProfile made) = Import();

        List<BuffSlot> slots = made.PartyBuffs!.Slots;
        Assert.Equal(new[] { "grze", "dfav", "prev" }, slots.Select(s => s.Spell).ToArray());
        Assert.True(slots[0].CastOnSelf);
        Assert.False(slots[0].AllMembers);
        // Listed for yourself and for the party: one slot that does both.
        Assert.True(slots[1].CastOnSelf);
        Assert.True(slots[1].AllMembers);
        Assert.False(slots[2].CastOnSelf);
        Assert.True(slots[2].AllMembers);
        Assert.All(slots, s => Assert.Equal(40, s.BlessIfAboveMa));
        Assert.All(slots, s => Assert.False(s.BlessWhileResting));
        Assert.All(slots, s => Assert.True(s.BlessDuringCombat));
    }

    [Fact]
    public void OtherTabs_CarryOverWhatMeansTheSame()
    {
        (_, CharacterProfile made) = Import();

        GeneralSettings general = Section<GeneralSettings>(made, "General");
        Assert.True(general.AutoMode.AutoCombat);
        Assert.False(general.AutoMode.AutoHeal);
        Assert.False(general.AutoMode.AutoRest);      // MegaMUD's Auto-Heal covers resting
        Assert.False(general.AutoMode.AutoSneak);
        Assert.False(general.AutoModeBase!.AutoHeal);

        CashSettings cash = Section<CashSettings>(made, "Cash");
        Assert.Equal(CashPolicy.Ignore, cash.CopperPolicy);
        Assert.Equal(CashPolicy.Collect, cash.GoldPolicy);
        Assert.True(cash.SkipCollectIfMakesHeavy);
        Assert.Equal("1/297", cash.BankRoomKey);
        Assert.Equal(0, cash.AutoDepositIfWealthExceeds);   // different units: left for the user

        TalkSettings talk = Section<TalkSettings>(made, "Talk");
        Assert.True(talk.DisallowRemoteFromGangpaths);
        Assert.Equal("command invalid or not allowed", talk.RemoteCommandFailureMessage);

        PartySettings party = Section<PartySettings>(made, "Party");
        Assert.Equal("mahe", party.MinorPartyHealSpell);
        Assert.Equal(80, party.MinorHealMemberThresholdPercent);
        Assert.Equal("mrai", party.MinorPartyHealAoeSpell);   // MegaMUD's one area heal is our minor
        Assert.Null(party.MajorPartyHealAoeSpell);
        Assert.Equal(2, party.AoeMinMembers);
        Assert.Equal(PartyRank.Front, party.Rank);            // MegaMUD's 0

        Assert.Equal(3, Section<OtherSettings>(made, "Other").MaxPickAttempts);
    }

    // The review accounts for what isn't carried over, with a reason, and never
    // shows the login itself.
    [Fact]
    public void Review_ListsWhatIsLeftBehind_AndNeverTheLogin()
    {
        (MegaMudImportPlan plan, _) = Import();

        string[] leftBehind = plan.Lines.Where(l => !l.WasImported).Select(l => l.Setting).ToArray();
        Assert.Contains("Normal weapon", leftBehind);
        Assert.Contains("Wealth limits", leftBehind);
        Assert.Contains("PvP settings", leftBehind);
        Assert.Contains("Favourite rooms", leftBehind);
        Assert.Contains("Area heal threshold", leftBehind);
        Assert.All(plan.Lines.Where(l => !l.WasImported), l => Assert.NotEqual(string.Empty, l.Note));
        // An untouched damage ceiling isn't worth a line.
        Assert.DoesNotContain("Attack spell max damage", leftBehind);

        Assert.True(plan.HasLogin);
        Assert.Equal("Some Board", plan.MegaMudBbsName);
        Assert.DoesNotContain(plan.Lines, l => l.Value.Contains("testuser") || l.Value.Contains("not-a-real-password"));
    }

    [Fact]
    public void Login_IsStoredEncrypted_OnlyWhenAsked()
    {
        (_, CharacterProfile with) = Import(withLogin: true);
        BbsCredentials login = with.BbsCredentials!["Paradigm"];
        Assert.False(string.IsNullOrEmpty(login.EncryptedUsername));
        Assert.DoesNotContain("testuser", login.EncryptedUsername);
        Assert.DoesNotContain("not-a-real-password", login.EncryptedPassword);

        (_, CharacterProfile without) = Import(withLogin: false);
        Assert.Null(without.BbsCredentials);
    }

    // The redial and cleanup settings are the board's, so the character import
    // leaves them alone and ApplyToBbs writes them, held to what BBS settings takes.
    [Fact]
    public void RedialAndCleanup_GoOntoTheBbs_WithinItsLimits()
    {
        (MegaMudImportPlan plan, _) = Import();
        Assert.True(plan.HasBbsSettings);

        var bbs = new MudPlay.Models.Settings.BbsProfile();
        plan.ApplyToBbs(bbs);

        Assert.Equal(1, bbs.MaxRedials);                // 0 in the file: at least one
        Assert.Equal(45, bbs.RedialPauseSeconds);
        Assert.True(bbs.ReconnectOnFailedConnect);
        Assert.True(bbs.ReconnectOnCarrierLost);
        Assert.False(bbs.ReconnectOnNoResponse);
        Assert.True(bbs.ReconnectAfterCleanup);
        Assert.Equal(600, bbs.CleanupPeriodMinutes);    // 5000 in the file: the most it takes

        MegaMudImportLine cleanup = plan.Lines.Single(l => l.Setting == "Cleanup period");
        Assert.Equal(MegaMudProfileImporter.BbsGroup, cleanup.Group);
        Assert.Equal("600 minutes", cleanup.Value);
    }

    [Fact]
    public void AFileWithNoRedialSettings_OffersNoBbsSettings()
    {
        MegaMudImportPlan plan = MegaMudProfileImporter.Read(MegaMudIni.Parse("[Health]\r\nHpFull%=86\r\n"), "X");
        Assert.False(plan.HasBbsSettings);
    }

    // Every carried-over setting names the value it came from and how it is edited,
    // so the review can hand a changed value back.
    [Fact]
    public void CarriedOverSettings_SayHowTheyAreEdited()
    {
        (MegaMudImportPlan plan, _) = Import();

        MegaMudImportLine restHp = plan.Lines.Single(l => l.Setting == "Rest until HP is at");
        Assert.Equal(MegaMudImportEdit.Number, restHp.Edit);
        Assert.Equal("90", restHp.EditValue);
        Assert.Equal("%", restHp.Unit);
        Assert.Equal("90%", restHp.Value);

        Assert.Equal(MegaMudImportEdit.Flag, plan.Lines.Single(l => l.Setting == "Auto-Combat").Edit);
        Assert.Equal(MegaMudImportEdit.Text, plan.Lines.Single(l => l.Setting == "Major heal spell").Edit);
        Assert.Equal(MegaMudImportEdit.Text, plan.Lines.Single(l => l.Setting == "Self bless 1").Edit);
        Assert.Equal(MegaMudImportEdit.Text, plan.Lines.Single(l => l.Setting == "Bank room (map/room)").Edit);

        MegaMudImportLine[] editable = plan.Lines.Where(l => l.Edit != MegaMudImportEdit.None).ToArray();
        Assert.All(editable, l => Assert.NotEqual(string.Empty, l.EditKey));
        Assert.Equal(editable.Length, editable.Select(l => l.EditKey).Distinct().Count());
    }

    [Fact]
    public void ValuesChangedInTheReview_AreWhatIsImported()
    {
        (MegaMudImportPlan plan, _) = Import();
        string Key(string setting) => plan.Lines.Single(l => l.Setting == setting).EditKey;

        var edits = new Dictionary<string, string>
        {
            [Key("Rest until HP is at")] = "95",
            [Key("Auto-Combat")] = "0",
            [Key("Major heal spell")] = "heal",
            [Key("Pre-rest command")] = "",
            [Key("Self bless 1")] = "bles",
            [Key("Bank room (map/room)")] = "2/14",
            [Key("Invalid remote command reply")] = "no",
            [Key("Most monsters in a room to fight")] = "99",
            [Key("Seconds between redials")] = "20",
            [Key("Party rank")] = "Back",
        };
        (_, CharacterProfile made) = Import(edits: edits);

        HealthSettings health = Section<HealthSettings>(made, "Health");
        Assert.Equal(95, health.RestMaxHp);
        Assert.Equal(50, health.RestIfBelowHp);                       // untouched
        Assert.True(string.IsNullOrEmpty(health.PreRestCommand));     // cleared in the review
        Assert.False(Section<GeneralSettings>(made, "General").AutoMode.AutoCombat);
        Assert.Equal("heal", Section<SpellsSettings>(made, "Spells").MajorHealSpell);
        Assert.Equal("bles", made.PartyBuffs!.Slots[0].Spell);
        Assert.Equal("2/14", Section<CashSettings>(made, "Cash").BankRoomKey);
        Assert.Equal("no", Section<TalkSettings>(made, "Talk").RemoteCommandFailureMessage);
        Assert.Equal(20, Section<CombatSettings>(made, "Combat").MaxMonstersInRoom);   // still held to a room's 20

        // The review itemises the active profile, so that is the one an edit to a
        // Combat / Health / Spells value changes.
        CombatProfileSettings profiles = made.CombatProfiles!;
        Assert.Equal(95, profiles.Profiles[1].Health.RestMaxHp);
        Assert.Equal(86, profiles.Profiles[0].Health.RestMaxHp);
        Assert.Equal("heal", profiles.Profiles[1].Spells.MajorHealSpell);
        Assert.Equal("grhe", profiles.Profiles[0].Spells.MajorHealSpell);

        var bbs = new MudPlay.Models.Settings.BbsProfile();
        plan.ApplyToBbs(bbs, edits);
        Assert.Equal(20, bbs.RedialPauseSeconds);
        Assert.Equal(PartyRank.Back, Section<PartySettings>(made, "Party").Rank);
    }

    [Theory]
    [InlineData("0", PartyRank.Front)]
    [InlineData("1", PartyRank.Mid)]
    [InlineData("2", PartyRank.Back)]
    public void PartyRank_ComesAcrossByMegaMudsNumber(string number, PartyRank expected)
    {
        MegaMudImportPlan plan = MegaMudProfileImporter.Read(MegaMudIni.Parse($"[Party]\r\nPartyRank={number}\r\n"), "X");
        var made = new CharacterProfile();
        string keyFile = Path.Combine(Path.GetTempPath(), "mudplay-megamud-import-" + Path.GetRandomFileName());
        plan.ApplyTo(made, "Board", withLogin: false, new PasswordProtector(keyFile));

        Assert.Equal(expected, Section<PartySettings>(made, "Party").Rank);
        MegaMudImportLine line = plan.Lines.Single(l => l.Setting == "Party rank");
        Assert.Equal(MegaMudImportEdit.Choice, line.Edit);
        Assert.Equal(new[] { "Front", "Mid", "Back" }, line.Choices);
    }

    [Fact]
    public void AnUnknownPartyRankNumber_IsLeftBehind()
    {
        MegaMudImportPlan plan = MegaMudProfileImporter.Read(MegaMudIni.Parse("[Party]\r\nPartyRank=7\r\n"), "X");
        Assert.False(plan.Lines.Single(l => l.Setting == "Party rank").WasImported);
    }

    // A pre / post rest command is flagged for a second look, and strongly when it
    // changes gear, which is the Equipment Manager's job here.
    [Fact]
    public void RestCommands_AreFlagged_AndStronglyWhenTheyChangeGear()
    {
        (MegaMudImportPlan plan, _) = Import();
        MegaMudImportLine pre = plan.Lines.Single(l => l.Setting == "Pre-rest command");

        MegaMudImportAdvice asImported = pre.Advise!(pre.EditValue)!;
        Assert.True(asImported.Strong);
        Assert.Contains("Equipment Manager", asImported.Text);

        Assert.True(MegaMudProfileImporter.RestCommandAdvice("wea robe")!.Strong);
        Assert.True(MegaMudProfileImporter.RestCommandAdvice("sit^Mwear ring^M")!.Strong);
        Assert.True(MegaMudProfileImporter.RestCommandAdvice("REMOVE helm")!.Strong);
        Assert.True(MegaMudProfileImporter.RestCommandAdvice("equip staff")!.Strong);
        // Flagged, but not as a gear swap: the verb has to be the command's own word.
        Assert.False(MegaMudProfileImporter.RestCommandAdvice("gos resting, remember me")!.Strong);
        Assert.False(MegaMudProfileImporter.RestCommandAdvice("weave")!.Strong);
        // Cleared in the review: nothing left to warn about.
        Assert.Null(MegaMudProfileImporter.RestCommandAdvice("  "));
        // Other text settings carry no caution.
        Assert.Null(plan.Lines.Single(l => l.Setting == "Major heal spell").Advise);
    }
}
