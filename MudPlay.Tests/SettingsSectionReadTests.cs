using System.IO;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.ViewModels.Settings;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace MudPlay.Tests;

// Every reader of a settings section reads it the same way (SettingsSection): an
// enum written by name, a hand edit or a MegaMUD import, used to fail the whole
// section for the engines, which then ran on defaults with nothing said. Profiles
// are written to scratch tier files and loaded the way the client loads them.
[Collection(GlobalSettingsFileCollection.Name)]
public sealed class SettingsSectionReadTests : IDisposable
{
    private readonly string _bbs = "test-sections-" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _warns = new();

    public SettingsSectionReadTests()
    {
        ScratchRoot.Require();
        SettingsValueGuard.Clamped += OnClamped;
    }

    public void Dispose()
    {
        SettingsValueGuard.Clamped -= OnClamped;
        if (Directory.Exists(AppPaths.BbsFolder(_bbs))) Directory.Delete(AppPaths.BbsFolder(_bbs), true);
    }

    private void OnClamped(string line)
    {
        lock (_warns) _warns.Add(line);
    }

    private List<string> Warns()
    {
        lock (_warns) return _warns.ToList();
    }

    private string WriteProfile(string json)
    {
        new BbsProfileStore().Save(new Models.Settings.BbsProfile { Name = _bbs });
        string path = AppPaths.CharacterProfileFile(_bbs, "Hero");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    private ProfileService LoadHero()
    {
        ProfileService profiles = TestProfiles.Service();
        profiles.Load(_bbs, "Hero");
        return profiles;
    }

    // "Absolute" by name: the engines (HealthManager, CastingDirector, the cash
    // pickup) read it, the Settings window shows it, and the bug report's resolved
    // copy agrees. Before, the engines read the whole Health and Cash sections as
    // defaults while the bug report showed the file's values.
    [Fact]
    public void EnumWrittenByName_ReadsTheSameInTheEnginesTheWindowAndTheResolver()
    {
        WriteProfile("""
            { "Name": "Hero", "Settings": {
                "Health": { "HpThresholdMode": "Absolute", "RestMaxHp": 500 },
                "Cash": { "CopperPolicy": "Collect", "RunicPolicy": "Ignore" } } }
            """);
        ProfileService profiles = LoadHero();

        HealthSettings health = SettingsSection.Read<HealthSettings>(profiles.Current, "Health");
        Assert.Equal(ThresholdMode.Absolute, health.HpThresholdMode);
        Assert.Equal(500, health.RestMaxHp);
        CashSettings cash = SettingsSection.Read<CashSettings>(profiles.Current, "Cash");
        Assert.Equal(CashPolicy.Collect, cash.CopperPolicy);

        using CashSectionViewModel window = new(profiles);
        Assert.Equal(CashPolicy.Collect, window.CopperPolicy);
        Assert.Equal(CashPolicy.Ignore, window.RunicPolicy);

        SettingsResolver resolver = new(new SettingsService(), new BbsProfileStore(), profiles);
        Assert.Equal(ThresholdMode.Absolute, resolver.Resolve<HealthSettings>("Health").HpThresholdMode);
        Assert.DoesNotContain(Warns(), w => w.Contains("Hero", StringComparison.Ordinal));
    }

    // A read-modify-write engine (an @auto-combat from a party member) used to read a
    // General section it couldn't parse as all defaults and write that back: the
    // character's DisableHangups and default task were gone from the file.
    [Fact]
    public void RemoteToggle_OnAGeneralSectionWithEnumNames_KeepsTheRestOfIt()
    {
        WriteProfile("""
            { "Name": "Hero", "Settings": { "General": { "DefaultTask": "BeginLoop", "DisableHangups": true } } }
            """);
        ProfileService profiles = LoadHero();
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), players);
        engine.SetWireSender(_ => { });
        players.RecordObservation("Tank", null, null, null, null, null, null, DateTime.UtcNow);
        players.EditCustomization("Tank", new PlayerCustomization(RemoteControls: PlayerRemoteControls.AlterSettings));
        using AutoModeRemoteHandler handler = new(engine, profiles, new AutoModeController(profiles));

        ChatLines.Hear(router, new ChatLine(ChatChannel.TelepathIncoming, "Tank", "@auto-combat off"));

        GeneralSettings general = LoadHero().Current!.Settings!["General"]
            .Deserialize<GeneralSettings>(JsonStore.Options)!;
        Assert.False(general.AutoMode.AutoCombat);
        Assert.True(general.DisableHangups);
        Assert.Equal(InitialTask.BeginLoop, general.DefaultTask);
    }

    // One field the section can't take costs that field, said once; the rest of the
    // section is what the engines read.
    [Fact]
    public void OneBadField_LeavesTheRestOfTheSection_WithAWarn()
    {
        string path = WriteProfile("""
            { "Name": "Hero", "Settings": { "Health": { "HpThresholdMode": "Sideways", "RestMaxHp": 500, "RunIfBelowHp": 33 } } }
            """);
        ProfileService profiles = LoadHero();

        HealthSettings health = SettingsSection.Read<HealthSettings>(profiles.Current, "Health");

        Assert.Equal(new HealthSettings().HpThresholdMode, health.HpThresholdMode);
        Assert.Equal(500, health.RestMaxHp);
        Assert.Equal(33, health.RunIfBelowHp);
        Assert.Contains(Warns(), w => w.Contains(path, StringComparison.Ordinal)
                                    && w.Contains("Settings.Health.HpThresholdMode", StringComparison.Ordinal));
    }

    // A section that isn't a section at all reads as its defaults, and says so,
    // naming the tier and the section; read again, it isn't said again.
    [Fact]
    public void UnreadableSection_ReadsAsDefaults_WithOneWarnNamingIt()
    {
        WriteProfile("""
            { "Name": "Hero", "Settings": { "Health": 5, "Party": [ 1, 2 ] } }
            """);
        ProfileService profiles = LoadHero();

        HealthSettings health = SettingsSection.Read<HealthSettings>(profiles.Current, "Health");
        SettingsSection.Read<HealthSettings>(profiles.Current, "Health");
        PartySettings party = SettingsSection.Read<PartySettings>(profiles.Current, "Party");

        Assert.Equal(new HealthSettings().RestMaxHp, health.RestMaxHp);
        Assert.Equal(new PartySettings().Rank, party.Rank);
        List<string> warns = Warns();
        Assert.Single(warns, w => w.StartsWith("Character (Hero) section Health couldn't be read", StringComparison.Ordinal)
                                  && w.Contains("using all of its defaults", StringComparison.Ordinal));
        Assert.Single(warns, w => w.StartsWith("Character (Hero) section Party couldn't be read", StringComparison.Ordinal));
    }

    // Each section as the client writes it is read back whole by the shared reader,
    // with its enums on disk as numbers (what every file has always held, and what an
    // older client reads), and nothing to warn about.
    [Fact]
    public void EverySection_RoundTripsThroughTheSharedReader()
    {
        string path = WriteProfile("""{ "Name": "Hero" }""");
        ProfileService profiles = LoadHero();
        profiles.Current!.Settings ??= new();
        foreach ((string key, Type type) in SettingsValueGuard.Sections)
            profiles.Current.Settings[key] = SettingsSection.ToElement(Activator.CreateInstance(type));
        profiles.Save();

        ProfileService reloaded = LoadHero();
        System.Reflection.MethodInfo read = typeof(SettingsSection).GetMethod(nameof(SettingsSection.Read),
            new[] { typeof(CharacterProfile), typeof(string) })!;
        foreach ((string key, Type type) in SettingsValueGuard.Sections)
        {
            object back = read.MakeGenericMethod(type).Invoke(null, new object?[] { reloaded.Current, key })!;
            Assert.Equal(JsonSerializer.Serialize(Activator.CreateInstance(type), type, JsonStore.Options),
                         JsonSerializer.Serialize(back, type, JsonStore.Options));
            // What an older client reads with the serializer's defaults.
            JsonSerializer.Deserialize(reloaded.Current!.Settings![key], type);
        }
        string text = File.ReadAllText(path);
        Assert.Contains("\"HpThresholdMode\": 0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Percentage\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Warns(), w => w.Contains("Hero", StringComparison.Ordinal));
    }

    // The structural rule: no production code reads or writes a section with the
    // serializer directly. A reader with the serializer's defaults is what failed the
    // sections above, and a writer outside SettingsSection may write enums by name.
    [Fact]
    public void NoProductionCode_ReadsOrWritesASectionAroundTheSharedReader()
    {
        HashSet<string> sectionTypes = SettingsValueGuard.Sections.Values.Select(t => t.FullName!).ToHashSet(StringComparer.Ordinal);
        string[] allowed = { typeof(SettingsSection).FullName!, typeof(SettingsValueGuard).FullName! };
        using AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(typeof(SettingsSection).Assembly.Location);

        List<string> violations = new();
        int sharedReads = 0;
        foreach (TypeDefinition type in asm.MainModule.GetTypes())
        {
            TypeDefinition outer = type;
            while (outer.DeclaringType is not null) outer = outer.DeclaringType;
            bool isAllowed = allowed.Contains(outer.FullName);
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction instr in method.Body.Instructions)
                {
                    if (instr.Operand is not GenericInstanceMethod call) continue;
                    if (call.DeclaringType.FullName == typeof(SettingsSection).FullName && call.Name == nameof(SettingsSection.Read))
                    {
                        sharedReads++;
                        continue;
                    }
                    if (isAllowed || call.DeclaringType.FullName != "System.Text.Json.JsonSerializer") continue;
                    if (!call.Name.StartsWith("Deserialize", StringComparison.Ordinal)
                        && !call.Name.StartsWith("SerializeTo", StringComparison.Ordinal)) continue;
                    TypeReference arg = call.GenericArguments[0];
                    bool section = sectionTypes.Contains(arg.FullName);
                    // A generic reader of whatever section it is handed, from the stored element.
                    bool anySection = arg.IsGenericParameter && call.Name.StartsWith("Deserialize", StringComparison.Ordinal)
                        && call.Parameters.Count > 0 && call.Parameters[0].ParameterType.FullName == "System.Text.Json.JsonElement";
                    if (section || anySection)
                        violations.Add($"{type.FullName}::{method.Name} calls JsonSerializer.{call.Name}<{arg.Name}>");
                }
        }

        Assert.True(violations.Count == 0, "Section read or written around SettingsSection:\n  " + string.Join("\n  ", violations));
        Assert.True(sharedReads > 30, $"only {sharedReads} shared reads found: the scan is looking in the wrong place");
    }
}
