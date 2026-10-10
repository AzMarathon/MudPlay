using System.Globalization;
using System.Text.Json;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Turns a MegaMUD character file into a MudPlay character. Only settings whose
// meaning is the same on both sides are carried over, each under the MudPlay name it
// lands as; everything else is listed as left behind with the reason, so the review
// shows the whole file accounted for and nothing is guessed at. Spell slots copy
// straight across: both clients name a spell by its short code.
//
// MegaMUD keeps Combat, Health and Spells once as the values in play and once per
// profile (`[Combat.P1]` …, named in `[Profiles]`). Each becomes a MudPlay combat
// profile, which is likewise a whole combat posture, and the one MegaMUD had active
// is the one made active here.
//
// Read builds the review; the plan it returns writes the same values onto a new
// character when the user accepts it.
public static class MegaMudProfileImporter
{
    public static MegaMudImportPlan Read(MegaMudIni ini, string suggestedName)
    {
        ArgumentNullException.ThrowIfNull(ini);
        var lines = new List<MegaMudImportLine>();
        // A dry run over a blank character: the same code that later writes the
        // real one, so the review can't say anything the import doesn't do.
        Apply(ini, new CharacterProfile(), lines, edits: null);
        var bbsLines = new List<MegaMudImportLine>();
        MapBbs(new Pass(ini, bbsLines, edits: null), new Models.Settings.BbsProfile());
        lines.AddRange(bbsLines);
        string? user = Clean(ini.Get("BBS", "UserID"));
        string? password = Clean(ini.Get("BBS", "Password"));
        if (user is not null || password is not null)
            lines.Add(new MegaMudImportLine("Login", "BBS user ID and password",
                "for the BBS this character is added to (optional, see the tick box)"));
        return new MegaMudImportPlan(
            string.IsNullOrWhiteSpace(suggestedName) ? "Imported" : suggestedName.Trim(),
            Clean(ini.Get("BBS", "BbsName")), user, password, lines,
            (profile, edits) => Apply(ini, profile, lines: null, edits),
            bbsLines.Any(static l => l.WasImported) ? (bbs, edits) => MapBbs(new Pass(ini, null, edits), bbs) : null);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // edits: what the user changed in the review, by MegaMudImportLine.EditKey. Each
    // stands in for the file's own value and goes through the same mapping.
    private static void Apply(MegaMudIni ini, CharacterProfile profile, List<MegaMudImportLine>? lines,
        IReadOnlyDictionary<string, string>? edits)
    {
        var pass = new Pass(ini, lines, edits);

        MapGeneral(pass, profile);
        MapCombatProfiles(pass, profile);
        MapBuffs(pass, profile);
        MapCash(pass, profile);
        MapTalk(pass, profile);
        MapOther(pass, profile);
        NoteTheRest(pass);
    }

    // ----- General: the auto-engine switches -------------------------------

    private static void MapGeneral(Pass p, CharacterProfile profile)
    {
        GeneralSettings general = Section<GeneralSettings>(profile, "General");
        AutoActionDefaults auto = general.AutoMode;
        const string g = "General";
        p.Flag("MegaMud", "AutoCombat", g, "Auto-Combat", v => auto.AutoCombat = v);
        p.Flag("MegaMud", "AutoNuke", g, "Auto-Nuke", v => auto.AutoNuke = v);
        // MegaMUD's one Auto-Heal switch covers resting too.
        p.Flag("MegaMud", "AutoHeal", g, "Auto-Heal and Auto-Rest", v => { auto.AutoHeal = v; auto.AutoRest = v; });
        p.Flag("MegaMud", "AutoBless", g, "Auto-Bless", v => auto.AutoBless = v);
        p.Flag("MegaMud", "AutoLight", g, "Auto-Light", v => auto.AutoLight = v);
        p.Flag("MegaMud", "AutoCash", g, "Auto-Get Cash", v => auto.AutoGetCash = v);
        p.Flag("MegaMud", "AutoGet", g, "Auto-Get Items", v => auto.AutoGetItems = v);
        p.Flag("MegaMud", "AutoSearch", g, "Auto-Search", v => auto.AutoSearch = v);
        p.Flag("MegaMud", "AutoSneak", g, "Auto-Sneak", v => auto.AutoSneak = v);
        p.Flag("MegaMud", "AutoHide", g, "Auto-Hide", v => auto.AutoHide = v);
        p.Flag("MegaMud", "AutoConnect", g, "Connect when the profile loads", v => general.AutoConnect = v);
        p.Flag("MegaMud", "BackupData", g, "Back up on save", v => general.BackupOnSave = v);
        p.Flag("Other", "HangupAllOff", g, "Allow hangup in All-Off mode", v => general.AllowHangupInAllOffMode = v);
        // The boxes on the General tab and the live toolbar state start out the same.
        general.AutoModeBase = JsonSerializer.Deserialize<AutoActionDefaults>(
            JsonSerializer.Serialize(auto, JsonStore.Options), JsonStore.Options);
        p.Skip("MegaMud", "AutoPreAttack", g, "Auto pre-attack switch", "no separate switch: a pre-attack spell casts while Auto-Nuke is on");
        p.Skip("MegaMud", "AutoTrack", g, "Auto-Track", "tracking is a PvP feature in MudPlay, with no PvE use; switch it on in Settings → PvP");
        p.Skip("MegaMud", "DefaultLoop", g, "Default loop", "MegaMUD loop files are imported separately (Navigation Management → Import .mp)");
        Put(profile, "General", general);
    }

    // ----- Combat / Health / Spells / Party, once per MegaMUD profile -------

    private static void MapCombatProfiles(Pass p, CharacterProfile profile)
    {
        var numbers = new List<int>();
        for (int n = 1; p.Ini.HasSection($"Combat.P{n}"); n++)
            if (p.Ini.Get($"Combat.P{n}", "Enabled") != "0") numbers.Add(n);
        int active = int.TryParse(p.Ini.Get("Profiles", "ActiveProfile"), out int a) && numbers.Contains(a) ? a : 0;
        // No profiles in the file: one, from the values in play.
        if (numbers.Count == 0) numbers.Add(0);
        if (!numbers.Contains(active)) active = numbers[0];

        var profiles = new List<CombatSpellProfile>();
        string activeId = string.Empty;
        foreach (int n in numbers)
        {
            bool live = n == active;
            // Only the active profile is itemised in the review; the others get a
            // line each below.
            Pass each = live ? p : p.Quiet();
            CombatSettings combat = Section<CombatSettings>(profile, "Combat");
            HealthSettings health = Section<HealthSettings>(profile, "Health");
            SpellsSettings spells = Section<SpellsSettings>(profile, "Spells");
            PartySettings party = Section<PartySettings>(profile, "Party");
            MapCombat(each.For("Combat", n), combat);
            MapHealth(each.For("Health", n), health);
            MapSpells(each.For("Spells", n), spells);
            MapParty(each, party);

            string name = Clean(p.Ini.Get("Profiles", $"Name{n}")) ?? (n == 0 ? "MegaMUD" : $"Profile {n}");
            CombatSpellProfile made = CombatSpellProfile.Capture(name, combat, health);
            made.Spells.CaptureFrom(spells);
            made.Party.CaptureFrom(party);
            profiles.Add(made);

            if (live)
            {
                activeId = made.Id;
                Put(profile, "Combat", combat);
                Put(profile, "Health", health);
                Put(profile, "Spells", spells);
                Put(profile, "Party", party);
            }
            if (numbers.Count > 1)
                p.Add("Combat profiles", name, live
                    ? "the active profile; its values are the ones listed under Combat, Health and Spells"
                    : $"attack command `{combat.NormalAttackCommand}`, with its own Combat, Health and Spells values");
        }
        profile.CombatProfiles = new CombatProfileSettings
        {
            Profiles = profiles,
            ActiveId = activeId,
            SchemaVersion = CombatProfileSettings.FullLoadoutVersion,
        };
    }

    private static void MapHealth(Pass p, HealthSettings h)
    {
        const string g = "Health";
        const string s = "Health";
        p.Int(s, "HpFull%", g, "Rest until HP is at", v => h.RestMaxHp = v, unit: "%");
        p.Int(s, "HpRest%", g, "Rest if HP is below", v => h.RestIfBelowHp = v, unit: "%");
        p.Int(s, "HpHeal%", g, "Heal while resting if HP is below", v => h.HealRestTrigger = v, unit: "%");
        p.Int(s, "HpHealAtt%", g, "Major heal in combat if HP is below", v => h.MajorHealCombatTrigger = v, unit: "%");
        p.Int(s, "HpHealMinor%", g, "Minor heal in combat if HP is below", v => h.MinorHealCombatTrigger = v, unit: "%");
        p.Int(s, "HpRun%", g, "Run if HP is below", v => h.RunIfBelowHp = v, unit: "%");
        p.Int(s, "HpLogoff%", g, "Hang up if HP is below", v => h.HangIfBelowHp = v, unit: "%");
        p.Int(s, "ManaFull%", g, "Rest until mana is at", v => h.RestMaxMa = v, unit: "%");
        p.Int(s, "ManaRest%", g, "Rest if mana is below", v => h.RestIfBelowMa = v, unit: "%");
        p.Int(s, "ManaHeal%", g, "Heal while resting only if mana is above", v => h.HealIfAboveMaResting = v, unit: "%");
        p.Int(s, "ManaHealAtt%", g, "Heal in combat only if mana is above", v => h.HealIfAboveMaCombat = v, unit: "%");
        p.Int(s, "ManaRun%", g, "Run if mana is below", v => h.RunIfBelowMa = v, unit: "%");
        p.Int(s, "ManaBless%", g, "Bless only if mana is above", v => h.BlessIfAboveMa = v, unit: "%");
        p.Flag(s, "UseMeditate", g, "Use the meditate ability", v => h.UseMeditateAbility = v);
        p.Flag(s, "MeditateB4Rest", g, "Meditate before resting", v => h.MeditateBeforeResting = v);
        h.HpThresholdMode = ThresholdMode.Percentage;
        h.MaThresholdMode = ThresholdMode.Percentage;

        // MegaMUD only sends these with its own "use pre / post commands" box ticked.
        // MudPlay reads ^M as a carriage return too, so the text goes across as it is.
        if (p.Raw(s, "PrePostRest") == "1")
        {
            p.Text(s, "PreRestCmd", g, "Pre-rest command", v => h.PreRestCommand = v, advice: RestCommandAdvice);
            p.Text(s, "PostRestCmd", g, "Post-rest command", v => h.PostRestCommand = v, advice: RestCommandAdvice);
        }
        else
        {
            p.Skip(s, "PreRestCmd", g, "Pre-rest command", "MegaMUD had its pre / post rest commands switched off");
            p.Skip(s, "PostRestCmd", g, "Post-rest command", "MegaMUD had its pre / post rest commands switched off");
        }
        if (p.Raw(s, "PreMedCmd") is { Length: > 0 } med && med != p.Raw(s, "PreRestCmd"))
            p.Skip(s, "PreMedCmd", g, "Pre-meditate command", "MudPlay has one pre-rest command for resting and meditating; the rest one was kept");
        if (p.Raw(s, "PostMedCmd") is { Length: > 0 } medPost && medPost != p.Raw(s, "PostRestCmd"))
            p.Skip(s, "PostMedCmd", g, "Post-meditate command", "MudPlay has one post-rest command for resting and meditating; the rest one was kept");
        p.SkipUnlessZero(s, "ManaHealMinor%", g, "Minor heal only if mana is above", "MudPlay has one mana gate for combat heals");
    }

    // A pre / post rest command in MegaMUD is very often a gear swap typed out by
    // hand. MudPlay swaps gear through the Equipment Manager's Pre-rest sets, and a
    // typed swap on top of that puts both in charge of the same slots.
    public static MegaMudImportAdvice? RestCommandAdvice(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        return SwapsGear(command)
            ? new MegaMudImportAdvice(
                "This command changes gear (rem / eq / wear). In MudPlay, gear swaps are the Equipment Manager's job: "
                + "build a Pre-rest HP or Pre-rest Mana set in the Workshop and clear this box, "
                + "or the command and the gear sets will both be changing what you wear.", Strong: true)
            : new MegaMudImportAdvice(
                "Check this is still wanted. If it is there to change gear for resting, leave it out: "
                + "the Equipment Manager's Pre-rest sets do that in MudPlay.", Strong: false);
    }

    private static bool SwapsGear(string command)
    {
        foreach (string step in command.Split(new[] { "^M", ";", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            if (Game.Inventory.GearCommandVerbs.IsGearVerb(step.Trim().Split(' ', 2)[0])) return true;
        return false;
    }

    private static void MapSpells(Pass p, SpellsSettings sp)
    {
        const string g = "Spells";
        const string s = "Spells";
        p.Text(s, "HealCmd", g, "Major heal spell", v => sp.MajorHealSpell = v);
        p.Text(s, "HealCmd2", g, "Minor heal spell", v => sp.MinorHealSpell = v);
        p.Text(s, "RegenCmd", g, "HP regen spell", v => sp.HpRegenSpell = v);
        p.Text(s, "FluxCmd", g, "Mana regen spell", v => sp.MaRegenSpell = v);
        p.Text(s, "BlindCmd", g, "Cure blindness spell", v => sp.CureBlindnessSpell = v);
        p.Text(s, "PoisonCmd", g, "Cure poison spell", v => sp.CurePoisonSpell = v);
        p.Text(s, "DiseaseCmd", g, "Cure disease spell", v => sp.CureDiseaseSpell = v);
        p.Text(s, "FreedomCmd", g, "Cure holds spell", v => sp.CureHoldsSpell = v);
        p.Text(s, "LightCmd", g, "Room light spell", v => sp.RoomLightSpell = v);
        p.Text(s, "HpFullCmd", g, "Cast when HP is full", v => sp.WhenHpFullSpell = v);
        p.Text(s, "MaFullCmd", g, "Cast when mana is full", v => sp.WhenMaFullSpell = v);
        p.Flag("Other", "IgnorePoison", g, "Ignore poison", v => sp.IgnorePoison = v);
        p.Flag("Other", "IgnoreBlind", g, "Ignore blindness", v => sp.IgnoreBlindness = v);
        p.Flag("Other", "IgnoreConfusion", g, "Ignore confusion", v => sp.IgnoreConfusion = v);
        p.SkipUnlessZero(s, "FluxMin", g, "Minimum mana flux", "MudPlay's mana-regen reroll is set per buff, in its own units");
    }

    private static void MapCombat(Pass p, CombatSettings c)
    {
        const string g = "Combat";
        const string s = "Combat";
        p.Text(s, "AttackCmd", g, "Attack command", v => c.NormalAttackCommand = v);
        Slot(p, "MultAttack", "ManaMultAtt%", "MultCastCnt", "MultMstrCnt", "Multi-attack (room) spell", c.MultiAttackSpell);
        Slot(p, "PreMultAttack", "ManaPreMultAtt%", "PreMultCastCnt", "PreMultMstrCnt", "Area debuff spell", c.AreaDebuffSpell);
        Slot(p, "PreAttack", "ManaPreAtt%", "PreCastCnt", null, "Single-target debuff spell", c.SingleTargetDebuffSpell);
        Slot(p, "AttackSpl", "ManaAttack%", "MaxCastCnt", null, "Attack spell", c.NormalAttackSpell);
        Slot(p, "AttackSpl2", "ManaAttack2%", "MaxCastCnt2", null, "Alternate attack spell", c.AlternateAttackSpell);
        c.SpellManaThresholdMode = ThresholdMode.Percentage;

        p.Flag(s, "CanBackStab", g, "Backstab", v => c.DoBackstab = v);
        p.Flag(s, "DontBsIfMulti", g, "Skip the backstab when multi-attacking", v => c.SkipBackstabIfMultiAttack = v);
        p.Flag(s, "RunIfBsFails", g, "Run if the backstab fails", v => c.RunIfBackstabFails = v);
        // A room holds 20 monsters at most, which is as high as MudPlay's cap goes.
        p.Int(s, "MaxMstrs", g, "Most monsters in a room to fight", v => c.MaxMonstersInRoom = Math.Clamp(v, 0, 20));
        p.Int(s, "MinMstrs", g, "Fewest monsters in a room to fight", v => c.MinMonstersInRoom = Math.Clamp(v, 0, 20));
        p.Int(s, "RunRooms", g, "Rooms to run", v => c.RunDistance = Math.Clamp(v, 1, 100));
        p.Flag("Other", "RunBackwards", g, "Run back the way you came", v => c.RunDirection = v ? RunDirection.Backward : RunDirection.Forward);
        p.Flag("Other", "BreakB4Running", g, "Break before running", v => c.BreakBeforeFleeing = v);

        foreach ((string key, string label) in new[]
                 {
                     ("NrmWeapon", "Normal weapon"), ("AltWeapon", "Alternate weapon"),
                     ("BsWeapon", "Backstab weapon"), ("Shield", "Shield"),
                 })
            p.Skip(s, key, g, label, "set weapons in the Workshop's Equipment Manager, where they are picked from the game's item list");
        p.Skip(s, "PreBsCmd", g, "Pre-backstab command", "MudPlay swaps to the Backstab gear set instead");
        p.Skip(s, "PostBsCmd", g, "Post-backstab command", "MudPlay swaps back from the Backstab gear set instead");
        p.SkipUnlessZero(s, "PoliteAttacks", g, "Polite attacks", "MudPlay's polite mode has more than on and off; choose it on the Combat tab");
        p.SkipUnlessZero(s, "ClearOnceEngaged", g, "Clear the room once engaged", "not the same rule as MudPlay's Kill all engaged; choose it on the Combat tab");
        p.SkipUnlessZero(s, "FailoverSpellAttacks", g, "Fail over spell attacks", "no matching MudPlay setting is mapped for it yet");
        foreach ((string key, string label) in new[]
                 {
                     ("MultMaxDmg", "Multi-attack max damage"), ("PreMultMaxDmg", "Area debuff max damage"),
                     ("PreMaxDmg", "Debuff max damage"), ("AttMaxDmg", "Attack spell max damage"),
                     ("AttMaxDmg2", "Alternate spell max damage"),
                 })
            p.SkipUnless(s, key, v => v != "99999" && v != "0", g, label, "MudPlay chooses spells by monster, not by a damage ceiling");
        foreach ((string key, string label) in new[]
                 {
                     ("PreMinExp", "Debuff minimum monster exp"), ("AttMinExp2", "Alternate spell minimum monster exp"),
                 })
            p.SkipUnlessZero(s, key, g, label, "MudPlay chooses spells by monster, not by exp");
        p.SkipUnless(s, "MaxMstrExp", v => v != "999999999" && v != "0", g, "Most monster exp to fight", "no matching MudPlay setting is mapped for it yet");

        void Slot(Pass pass, string spellKey, string manaKey, string castsKey, string? enemiesKey, string label, CombatSpellSlot slot)
        {
            if (!pass.Text(s, spellKey, g, label, v => slot.SpellName = v)) return;
            pass.Int(s, manaKey, g, $"{label}: only if mana is above", v => slot.MinManaPerCast = v, unit: "%");
            // MegaMUD's 0 is "no limit", which MudPlay stores as no number.
            pass.Int(s, castsKey, g, $"{label}: most casts per room (0 = no limit)", v =>
            {
                slot.MaxCastsPerRoom = v > 0 ? v : null;
                return Math.Max(0, v);
            });
            if (enemiesKey is not null)
                pass.Int(s, enemiesKey, g, $"{label}: fewest monsters", v => slot.MinEnemies = Math.Max(0, v));
        }
    }

    private static void MapParty(Pass p, PartySettings pt)
    {
        const string g = "Party";
        const string s = "Party";
        p.Int(s, "ParPeriod", g, "Seconds between `par` polls", v => pt.ParPollFrequencySec = Math.Clamp(v, 1, 60));
        p.Text(s, "PartyHeal1", g, "Minor party heal spell", v => pt.MinorPartyHealSpell = v);
        p.Int(s, "PartyHeal1%", g, "Minor party heal if a member is below", v => pt.MinorHealMemberThresholdPercent = v, unit: "%");
        p.Text(s, "PartyHeal2", g, "Major party heal spell", v => pt.MajorPartyHealSpell = v);
        p.Int(s, "PartyHeal2%", g, "Major party heal if a member is below", v => pt.MajorHealMemberThresholdPercent = v, unit: "%");
        // MegaMUD has one area heal; it goes in MudPlay's minor area-heal slot
        // (user, 2026-10-08), which fires on the minor party heal's threshold.
        if (p.Text(s, "PartyHealArea", g, "Minor party area heal spell", v => pt.MinorPartyHealAoeSpell = v))
        {
            p.Int(s, "PartyHealAreaAt", g, "Area heal: fewest hurt members", v => pt.AoeMinMembers = Math.Max(1, v));
            p.Skip(s, "PartyHealArea%", g, "Area heal threshold", "MudPlay's minor area heal fires on the minor party heal's threshold, above");
        }
        p.Int(s, "PartyMaxMstrs", g, "Most monsters in a room when partying", v => pt.MaxMonstersWhenPartying = Math.Clamp(v, 0, 20));
        p.Int(s, "PartyWait%", g, "Wait if a member's HP is below", v => pt.WaitIfMemberBelowPercent = v, unit: "%");
        p.Flag(s, "IgnoreWait", g, "Ignore @wait when leading", v => pt.IgnoreWaitWhenLeading = v);
        p.Flag(s, "SendPanic", g, "Use panic while leading", v => pt.UsePanicWhileLeading = v);
        p.Flag(s, "IgnorePanic", g, "Ignore panics", v => pt.IgnorePanics = v);
        p.Flag(s, "HelpBash", g, "Help the leader open doors", v => pt.HelpLeaderOpenDoors = v);
        p.Flag(s, "AskHealth", g, "Ask members for their health", v => pt.SendHealthToMembers = v);
        p.Flag("Other", "BlessResting", g, "Bless party while resting", v => pt.BlessWhileResting = v);
        p.Flag("Other", "BlessCombat", g, "Bless party during combat", v => pt.BlessDuringCombat = v);
        // MegaMUD numbers the ranks 0 front, 1 mid, 2 back (user, 2026-10-08: 0 seen
        // in a file, 1 and 2 taken from the order).
        if (!p.Choice(s, "PartyRank", g, "Party rank", RankNames, v => pt.Rank = Enum.Parse<PartyRank>(v)))
            p.Skip(s, "PartyRank", g, "Party rank", "not a rank number MudPlay knows; set Front / Mid / Back on the Party tab");
        foreach ((string key, string label) in new[]
                 {
                     ("AttackLast", "Attack last"), ("AttackLate", "Attack late"), ("AttackReverse", "Attack in reverse order"),
                     ("AttLeaderMstr", "Attack the leader's monster"),
                 })
            p.SkipUnlessZero(s, key, g, label, "choose target order and attack timing on the Combat tab");
        foreach ((string key, string label) in new[]
                 {
                     ("HealLowest", "Heal the lowest member first"), ("RestHealParty", "Heal the party while resting"),
                     ("AreaHealPriority", "Area heal priority"), ("ShareCash", "Share cash"), ("DefendParty", "Defend the party"),
                     ("ShareDamage", "Share damage"), ("IgnoreParty", "Ignore the party"),
                 })
            p.SkipUnlessZero(s, key, g, label, "no matching MudPlay setting is mapped for it yet");
    }

    private static readonly string[] RankNames = { nameof(PartyRank.Front), nameof(PartyRank.Mid), nameof(PartyRank.Back) };

    // ----- Buffs: self and party blesses become Buff Watchdog slots ---------

    private static void MapBuffs(Pass p, CharacterProfile profile)
    {
        profile.PartyBuffs ??= new BuffSettings();
        List<BuffSlot> slots = profile.PartyBuffs.Slots;
        int blessAbove = p.IntOr("Health", "ManaBless%", BuffSlot.DefaultBlessIfAboveMa);
        bool resting = p.Raw("Other", "BlessResting") == "1";
        bool combat = p.Raw("Other", "BlessCombat") != "0";

        for (int i = 1; i <= 10; i++)
            if (Clean(p.Raw("Spells", $"BlessCmd{i}")) is { } spell)
            {
                AddSlot(spell, self: true);
                p.Add("Buffs", $"Self bless {i}", spell, editKey: Pass.Key("Spells", $"BlessCmd{i}"));
            }
        for (int i = 1; i <= 4; i++)
            if (Clean(p.Raw("Party", $"PartyBless{i}")) is { } spell)
            {
                AddSlot(spell, self: false);
                p.Add("Buffs", $"Party bless {i} (cast on every member)", spell, editKey: Pass.Key("Party", $"PartyBless{i}"));
                p.Skip("Party", $"PartyBlessWait{i}", "Buffs", $"Party bless {i} wait", "MudPlay recasts a buff as its own timer runs out");
            }
        for (int i = 1; i <= 10; i++)
        {
            p.SkipUnlessZero("Spells", $"BlessPri{i}", "Buffs", $"Self bless {i} priority", "drag the row in the Buff Watchdog to order it");
            p.SkipUnlessZero("Spells", $"BlessTim{i}", "Buffs", $"Self bless {i} time", "MudPlay reads a buff's length from the game data");
        }

        // One slot per spell: a bless MegaMUD lists for yourself and for the party
        // is the same buff cast on both, which a single MudPlay slot does.
        void AddSlot(string spell, bool self)
        {
            BuffSlot? slot = slots.FirstOrDefault(x => string.Equals(x.Spell, spell, StringComparison.OrdinalIgnoreCase));
            if (slot is null)
            {
                slots.Add(slot = new BuffSlot
                {
                    Spell = spell,
                    BlessIfAboveMa = blessAbove,
                    BlessWhileResting = resting,
                    BlessDuringCombat = combat,
                });
            }
            if (self) slot.CastOnSelf = true;
            else slot.AllMembers = true;
        }
    }

    // ----- Cash -------------------------------------------------------------

    private static void MapCash(Pass p, CharacterProfile profile)
    {
        CashSettings cash = Section<CashSettings>(profile, "Cash");
        const string g = "Cash";
        const string s = "Cash";
        p.Flag(s, "WantCopper", g, "Collect copper", v => cash.CopperPolicy = Policy(v));
        p.Flag(s, "WantSilver", g, "Collect silver", v => cash.SilverPolicy = Policy(v));
        p.Flag(s, "WantGold", g, "Collect gold", v => cash.GoldPolicy = Policy(v));
        p.Flag(s, "WantPlat", g, "Collect platinum", v => cash.PlatinumPolicy = Policy(v));
        p.Flag(s, "WantRunic", g, "Collect runic", v => cash.RunicPolicy = Policy(v));
        p.Flag(s, "DontBeMedium", g, "Skip a pickup that would make you Medium", v => cash.SkipCollectIfMakesMedium = v);
        p.Flag(s, "DontBeHeavy", g, "Skip a pickup that would make you Heavy", v => cash.SkipCollectIfMakesHeavy = v);
        p.Flag(s, "GetAfterCombat", g, "Collect only after combat", v => cash.CollectAfterCombatFinished = v);
        p.Flag(s, "DropCoins", g, "Drop smaller coins for larger", v => cash.DropSmallerForLarger = v);
        p.Flag(s, "AutoRecoverCorpse", g, "Auto-recover death piles", v => profile.DeathAutoRecover = v);
        // "Bank of Godfrey-1 297": the room's name, then its map and room number.
        if (BankRoom(p.Raw(s, "Bank")) is { } bank)
            p.Add(g, "Bank room (map/room)", bank, () => cash.BankRoomKey = bank, editKey: Pass.Key(s, "Bank"));
        else
            p.Skip(s, "Bank", g, "Bank", "couldn't read a map and room number out of it");
        p.SkipUnlessZero(s, "StashCoin", g, "Stash coin", "mark your stash rooms on the map, then choose what to stash on the Cash tab");
        p.SkipUnlessZero(s, "LimitWealth", g, "Wealth limits", "MegaMUD and MudPlay count wealth in different units; set the deposit thresholds on the Cash tab");
        p.SkipUnlessZero(s, "LimitCoins", g, "Coin limit", "set the deposit thresholds on the Cash tab");
        p.SkipUnlessZero(s, "GetOnlyInCombat", g, "Collect only in combat", "no matching MudPlay setting is mapped for it yet");
        p.SkipUnlessZero(s, "AutoCollectLootbox", g, "Auto-collect loot boxes", "flag the box Auto-collect in Game Data → Items");
        Put(profile, "Cash", cash);

        static CashPolicy Policy(bool want) => want ? CashPolicy.Collect : CashPolicy.Ignore;
    }

    // MegaMUD's "Bank of Godfrey-1 297", or the "1/297" the review shows and takes
    // back when edited.
    private static string? BankRoom(string? value)
    {
        if (Clean(value) is not { } text) return null;
        int dash = text.LastIndexOf('-');
        string[] parts = text[(dash + 1)..].Split(new[] { ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out int map) && int.TryParse(parts[1], out int room)
            && map > 0 && room > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{map}/{room}")
            : null;
    }

    // ----- Talk -------------------------------------------------------------

    private static void MapTalk(Pass p, CharacterProfile profile)
    {
        TalkSettings talk = Section<TalkSettings>(profile, "Talk");
        const string g = "Talk";
        const string s = "Talk";
        p.Flag(s, "LogTalk", g, "Log conversations", v => talk.LogConversations = v);
        p.Flag(s, "GreetPlayers", g, "Greet players when first met", v => talk.GreetPlayersWhenFirstMet = v);
        p.Flag(s, "LookPlayers", g, "Look at players on arrival", v => talk.LookAtPlayersOnArrival = v);
        p.Flag(s, "NoRemoteCmds", g, "Disallow all remote commands", v => talk.DisallowAllRemoteCommands = v);
        p.Flag(s, "NoGangCmds", g, "Disallow remote commands from gangpaths", v => talk.DisallowRemoteFromGangpaths = v);
        p.Flag(s, "WarnRemote", g, "Reply to an invalid remote command", v => talk.WarnOnInvalidRemoteCommand = v);
        // MegaMUD stores the reply with the braces it is sent in; MudPlay adds them.
        p.Text(s, "CmdReply", g, "Invalid remote command reply", v => talk.RemoteCommandFailureMessage = v.Trim().Trim('{', '}').Trim(),
            v => v.Trim().Trim('{', '}').Trim());
        p.SkipUnlessZero(s, "AutoAfk", g, "Auto-AFK", "no matching MudPlay setting is mapped for it yet");
        Put(profile, "Talk", talk);
    }

    // ----- The BBS: redial and cleanup -------------------------------------
    // These belong to the board, shared by every character on it, so they are
    // written only when the user ticks them in the review (BbsGroup says so).

    public const string BbsGroup = "BBS (tick box)";

    private static void MapBbs(Pass p, Models.Settings.BbsProfile bbs)
    {
        const string g = BbsGroup;
        const string s = "Comms";
        // Held to what BBS settings itself accepts.
        p.Int(s, "RedialMax", g, "Most redials", v => bbs.MaxRedials = Math.Clamp(v, 1, 9999));
        p.Int(s, "RedialPause", g, "Seconds between redials", v => bbs.RedialPauseSeconds = Math.Clamp(v, 1, 300));
        p.Flag(s, "RedialConnect", g, "Redial when a connect attempt fails", v => bbs.ReconnectOnFailedConnect = v);
        p.Flag(s, "RedialCarrier", g, "Redial when the carrier is lost", v => bbs.ReconnectOnCarrierLost = v);
        p.Flag(s, "RedialNoResponse", g, "Redial when the server stops responding", v => bbs.ReconnectOnNoResponse = v);
        p.Flag(s, "RedialCleanup", g, "Redial after cleanup", v => bbs.ReconnectAfterCleanup = v);
        p.Int(s, "CleanupPeriod", g, "Cleanup period", v => bbs.CleanupPeriodMinutes = Math.Clamp(v, 0, 600), unit: " minutes");
        p.SkipUnlessZero(s, "LogoffLowExp", "BBS", "Log off on a low exp rate", "no matching MudPlay setting is mapped for it yet");
        p.SkipUnlessZero(s, "LagWait", "BBS", "Lag wait", "no matching MudPlay setting is mapped for it yet");
    }

    // ----- Other ------------------------------------------------------------

    private static void MapOther(Pass p, CharacterProfile profile)
    {
        OtherSettings other = Section<OtherSettings>(profile, "Other");
        const string g = "Other";
        const string s = "Other";
        p.Flag(s, "CanDisarmTraps", g, "Disarm traps if able", v => other.UtilizeDisarmTrapsIfAble = v);
        p.Int(s, "DisarmMax", g, "Most trap disarm attempts", v => other.MaxTrapDisarmAttempts = Math.Max(1, v));
        p.Int(s, "PickMax", g, "Most picklock attempts", v => other.MaxPickAttempts = Math.Max(1, v));
        p.Int(s, "SearchMax", g, "Most hidden-exit searches", v => other.MaxHiddenSearchAttempts = Math.Max(1, v));
        p.Skip(s, "BashMax", g, "Most door bash attempts", "no matching MudPlay setting is mapped for it yet");
        p.SkipUnlessZero(s, "AutoTrain", g, "Auto-train", "needs a training plan first: set it on the Workshop's CP Allocation tab");
        p.SkipUnlessZero(s, "HangupNaked", g, "Hang up if naked", "no matching MudPlay setting is mapped for it yet");
        p.SkipUnlessZero(s, "RelogInstead", g, "Relog instead of hanging up", "no matching MudPlay setting is mapped for it yet");
        p.Skip(s, "EntryCmd", g, "Realm entry command", "kept with the realm, in Profile Management → Realm settings");
        p.Skip(s, "ExitCmd", g, "Realm exit command", "kept with the realm, in Profile Management → Realm settings");
        Put(profile, "Other", other);
    }

    // Whole areas with no counterpart to map key by key.
    private static void NoteTheRest(Pass p)
    {
        Area("PvP", "PvP", "PvP settings", "MudPlay's PvP tab is laid out differently; set it up there");
        Area("Alerts", "Sounds", "Alert sounds", "pick sounds in Settings → Sounds");
        Area("Schedule", "Events", "Scheduled events", "MegaMUD's event format isn't read yet; re-create them in Settings → Events");
        Area("Auto-roam", "Navigation", "Auto-roam rooms and commands", "not carried over; in MudPlay, loops and Auto-Lair do the roaming");
        Area("Player", "Character", "Stats and level", "read from the game with `stat` when the character logs in");
        if (p.Ini.Keys("MegaMud").Any(k => k.StartsWith("FavRoom", StringComparison.OrdinalIgnoreCase)))
            p.Add("Navigation", "Favourite rooms", string.Empty, note: "MegaMUD stores them as room codes; add them from the map");
        p.Add("General", "Window positions, toolbar, colours", string.Empty, note: "MegaMUD's own layout; MudPlay keeps its own");

        void Area(string section, string group, string label, string why)
        {
            if (p.Ini.HasSection(section)) p.Add(group, label, string.Empty, note: why);
        }
    }

    // ----- Plumbing ---------------------------------------------------------

    private static T Section<T>(CharacterProfile profile, string key) where T : new() =>
        profile.Settings is { } settings && settings.TryGetValue(key, out JsonElement el)
            ? JsonSerializer.Deserialize<T>(el, JsonStore.Options) ?? new T()
            : new T();

    private static void Put<T>(CharacterProfile profile, string key, T value)
    {
        profile.Settings ??= new Dictionary<string, JsonElement>();
        profile.Settings[key] = JsonSerializer.SerializeToElement(value, JsonStore.Options);
    }

    // One walk over the file: reads values, sets them, and writes the review lines.
    // For(section, n) reads a profile's own copy of a section first and the values
    // in play second, since a `.P<n>` section leaves out a few of the base's keys.
    //
    // An edit from the review stands in for the file's value. Combat, Health and
    // Spells are reviewed for the active profile only, so an edit to one of those
    // reaches that profile alone (a quiet pass, which builds the others, ignores
    // it); an edit anywhere else reaches every profile, as the setting itself does.
    private sealed class Pass(MegaMudIni ini, List<MegaMudImportLine>? lines,
        IReadOnlyDictionary<string, string>? edits, string? baseSection = null, string? overlay = null, bool reviewed = true)
    {
        private static readonly string[] PerProfile = { "Combat", "Health", "Spells" };

        public MegaMudIni Ini => ini;

        public static string Key(string section, string key) => $"{section}|{key}";

        public Pass Quiet() => new(ini, null, edits, baseSection, overlay, reviewed: false);

        public Pass For(string section, int profileNumber) =>
            new(ini, lines, edits, section, profileNumber > 0 ? $"{section}.P{profileNumber}" : null, reviewed);

        public string? Raw(string section, string key)
        {
            if (edits is not null && (reviewed || !PerProfile.Contains(section, StringComparer.OrdinalIgnoreCase))
                && edits.TryGetValue(Key(section, key), out string? edited))
                return edited;
            return (overlay is not null && string.Equals(section, baseSection, StringComparison.OrdinalIgnoreCase)
                    ? ini.Get(overlay, key)
                    : null)
                ?? ini.Get(section, key);
        }

        public int IntOr(string section, string key, int fallback) =>
            int.TryParse(Raw(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        // A line the mapping worked out for itself. editKey makes it a text the
        // review can change.
        public void Add(string group, string setting, string value, Action? set = null, string note = "", string? editKey = null)
        {
            set?.Invoke();
            lines?.Add(new MegaMudImportLine(group, setting, value, note)
            {
                Edit = editKey is null ? MegaMudImportEdit.None : MegaMudImportEdit.Text,
                EditKey = editKey ?? string.Empty,
                EditValue = editKey is null ? string.Empty : value,
            });
        }

        // set returns the value it stored, which is what the review lists and edits:
        // a clamp shows as the number that will really be used.
        public bool Int(string section, string key, string group, string label, Func<int, int> set, string unit = "")
        {
            if (!int.TryParse(Raw(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return false;
            string stored = set(v).ToString(CultureInfo.InvariantCulture);
            lines?.Add(new MegaMudImportLine(group, label, stored + unit)
            {
                Edit = MegaMudImportEdit.Number, EditKey = Key(section, key), EditValue = stored, Unit = unit.Trim(),
            });
            return true;
        }

        public bool Flag(string section, string key, string group, string label, Action<bool> set)
        {
            if (Raw(section, key) is not { } raw || raw.Trim() is not ("0" or "1")) return false;
            bool on = raw.Trim() == "1";
            set(on);
            lines?.Add(new MegaMudImportLine(group, label, on ? "on" : "off")
            {
                Edit = MegaMudImportEdit.Flag, EditKey = Key(section, key), EditValue = on ? "1" : "0",
            });
            return true;
        }

        // The file holds the choice's position in the list; an edit from the review
        // holds its name.
        public bool Choice(string section, string key, string group, string label, IReadOnlyList<string> choices, Action<string> set)
        {
            string? raw = Raw(section, key)?.Trim();
            string? chosen = int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int at)
                ? at < choices.Count ? choices[at] : null
                : choices.FirstOrDefault(c => string.Equals(c, raw, StringComparison.OrdinalIgnoreCase));
            if (chosen is null) return false;
            set(chosen);
            lines?.Add(new MegaMudImportLine(group, label, chosen)
            {
                Edit = MegaMudImportEdit.Choice, EditKey = Key(section, key), EditValue = chosen, Choices = choices,
            });
            return true;
        }

        // Empty means the slot isn't used, which is what a fresh MudPlay setting is.
        public bool Text(string section, string key, string group, string label, Action<string> set,
            Func<string, string>? show = null, Func<string, MegaMudImportAdvice?>? advice = null)
        {
            if (Clean(Raw(section, key)) is not { } v) return false;
            set(v);
            string shown = show?.Invoke(v) ?? v;
            lines?.Add(new MegaMudImportLine(group, label, shown)
            {
                Edit = MegaMudImportEdit.Text, EditKey = Key(section, key), EditValue = shown,
                Advise = advice,
            });
            return true;
        }

        // Left behind, when the file has a value for it worth mentioning.
        public void Skip(string section, string key, string group, string label, string why) =>
            SkipUnless(section, key, static v => v.Length > 0, group, label, why);

        public void SkipUnlessZero(string section, string key, string group, string label, string why) =>
            SkipUnless(section, key, static v => v.Length > 0 && v != "0", group, label, why);

        public void SkipUnless(string section, string key, Func<string, bool> worthSaying, string group, string label, string why)
        {
            if (Raw(section, key)?.Trim() is not { } v || !worthSaying(v)) return;
            lines?.Add(new MegaMudImportLine(group, label, v, why));
        }
    }
}
