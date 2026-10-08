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
        [PvP]
        PvpAction=5
        """;

    private static (MegaMudImportPlan Plan, CharacterProfile Made) Import(bool withLogin = true)
    {
        MegaMudImportPlan plan = MegaMudProfileImporter.Read(MegaMudIni.Parse(Ini), "Cleric");
        var made = new CharacterProfile();
        string keyFile = Path.Combine(Path.GetTempPath(), "mudplay-megamud-import-" + Path.GetRandomFileName());
        plan.ApplyTo(made, "Paradigm", withLogin, new PasswordProtector(keyFile));
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
        Assert.Contains("Party rank", leftBehind);
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
}
