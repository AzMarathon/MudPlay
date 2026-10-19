using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A number in a settings file nobody would set (hand-edited, a bad import, a
// damaged save) is held to its field's range as the file is read, with a Warn, and
// one bad field costs that field only, never the whole file. Read through the
// stores the client reads them with.
[Collection(GlobalSettingsFileCollection.Name)]
public sealed class SettingsValueHardeningTests : IDisposable
{
    private readonly string _bbs = "test-hardening-" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _warns = new();

    public SettingsValueHardeningTests()
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

    private List<string> WarnsAbout(string path)
    {
        lock (_warns) return _warns.Where(w => w.Contains(path, StringComparison.Ordinal)).ToList();
    }

    private string WriteBbs(string json)
    {
        string path = AppPaths.BbsProfileFile(_bbs);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    private string WriteProfile(string json)
    {
        new BbsProfileStore().Save(new BbsProfile { Name = _bbs });
        string path = AppPaths.CharacterProfileFile(_bbs, "Hero");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    // The fuzz that found it: 100000 columns × 100000 rows overflowed the grid's
    // cols × rows into an allocation of some 34 GB, and two billion scrollback rows
    // failed the ring's array. The loaded values are the Settings window's limits,
    // and the screen built from them stays small.
    [Fact]
    public void BbsLoad_HugeTerminalGeometry_IsHeldToTheWindowsLimits_AndTheScreenStaysSmall()
    {
        string path = WriteBbs($$"""
            { "Name": "{{_bbs}}", "TerminalCols": 100000, "TerminalRows": 100000, "ScrollbackLines": 2147483647 }
            """);

        BbsProfile bbs = new BbsProfileStore().Get(_bbs)!;

        Assert.Equal(200, bbs.TerminalCols);
        Assert.Equal(100, bbs.TerminalRows);
        Assert.Equal(100_000, bbs.ScrollbackLines);
        List<string> warns = WarnsAbout(path);
        Assert.Contains(warns, w => w.Contains("BBS setting TerminalCols", StringComparison.Ordinal)
                                  && w.Contains("was 100000", StringComparison.Ordinal)
                                  && w.Contains("using 200", StringComparison.Ordinal));
        Assert.Contains(warns, w => w.Contains("ScrollbackLines", StringComparison.Ordinal)
                                  && w.Contains("2147483647", StringComparison.Ordinal));

        long before = GC.GetAllocatedBytesForCurrentThread();
        TerminalEmulator emulator = new(80, 25);
        emulator.Resize(bbs.TerminalCols, bbs.TerminalRows);
        emulator.Screen.Scrollback.SetCapacity(bbs.ScrollbackLines);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 16L * 1024 * 1024);
        Assert.Equal((200, 100), (emulator.Screen.Cols, emulator.Screen.Rows));
    }

    // Whatever slips past the load still can't size the grid or the ring past their
    // own bounds.
    [Fact]
    public void Screen_AndScrollback_AskedForTooMuch_StayWithinTheirBounds()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        TerminalEmulator emulator = new(80, 25);
        emulator.Resize(100_000, 100_000);
        emulator.Screen.Scrollback.SetCapacity(int.MaxValue);

        Assert.Equal((TerminalScreen.MaxCols, TerminalScreen.MaxRows), (emulator.Screen.Cols, emulator.Screen.Rows));
        Assert.Equal(ScrollbackBuffer.MaxCapacity, emulator.Screen.Scrollback.Capacity);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 128L * 1024 * 1024);
    }

    // 5.5 columns came up as a sliver of a terminal with nothing said: a fraction in
    // a whole-number field is rounded, then held to the range, and both are warned.
    [Fact]
    public void FractionalTerminalGeometryAndFontSize_LoadRoundedAndClamped_WithAWarn()
    {
        string profilePath = WriteProfile("""
            { "Name": "Hero", "Settings": { "General": { "TerminalFontSize": 5.5, "NavTooltipFontSize": 99 } } }
            """);
        string bbsPath = WriteBbs($$"""
            { "Name": "{{_bbs}}", "TerminalCols": 80.5, "TerminalRows": 5.5 }
            """);

        BbsProfile bbs = new BbsProfileStore().Get(_bbs)!;
        ProfileService profiles = TestProfiles.Service();
        CharacterProfile hero = profiles.Load(_bbs, "Hero");
        GeneralSettings general = hero.Settings!["General"].Deserialize<GeneralSettings>(JsonStore.Options)!;

        Assert.Equal(81, bbs.TerminalCols);
        Assert.Equal(20, bbs.TerminalRows);
        Assert.Equal(8, general.TerminalFontSize);
        Assert.Equal(32, general.NavTooltipFontSize);
        List<string> bbsWarns = WarnsAbout(bbsPath);
        Assert.Contains(bbsWarns, w => w.Contains("TerminalCols", StringComparison.Ordinal) && w.Contains("not a whole number", StringComparison.Ordinal));
        Assert.Contains(bbsWarns, w => w.Contains("TerminalRows", StringComparison.Ordinal) && w.Contains("using 20", StringComparison.Ordinal));
        Assert.Contains(WarnsAbout(profilePath), w => w.Contains("Character setting Settings.General.TerminalFontSize", StringComparison.Ordinal));
    }

    // A number its field can't hold, or text where a number goes, used to fail the
    // whole profile: the client came up on the default profile with the character's
    // workspace missing. Now that field alone takes its default.
    [Fact]
    public void ProfileLoad_OneUnreadableField_KeepsTheRestOfTheCharacter()
    {
        string path = WriteProfile("""
            {
              "Name": "Hero",
              "Favorites": [ { "Label": "Home", "Map": 1, "Room": 2 } ],
              "CorpseHandoffs": [ { "Coins": { "Copper": 1e300, "Gold": 3 } } ],
              "LastKnownStats": { "Strength": -2147483648, "Level": 12 },
              "Settings": { "Health": { "RestMaxHp": "lots", "RunIfBelowHp": 33 } }
            }
            """);

        CharacterProfile hero = TestProfiles.Service().Load(_bbs, "Hero");

        Assert.Equal("Hero", hero.Name);
        Assert.Equal("Home", Assert.Single(hero.Favorites!).Label);
        Assert.Equal(int.MaxValue, hero.CorpseHandoffs![0].Coins.Copper);
        Assert.Equal(3, hero.CorpseHandoffs[0].Coins.Gold);
        Assert.Equal(-1_000_000, hero.LastKnownStats!.Strength);
        Assert.Equal(12, hero.LastKnownStats.Level);
        HealthSettings health = hero.Settings!["Health"].Deserialize<HealthSettings>(JsonStore.Options)!;
        Assert.Equal(new HealthSettings().RestMaxHp, health.RestMaxHp);
        Assert.Equal(33, health.RunIfBelowHp);
        List<string> warns = WarnsAbout(path);
        Assert.Contains(warns, w => w.Contains("CorpseHandoffs[0].Coins.Copper", StringComparison.Ordinal));
        Assert.Contains(warns, w => w.Contains("Settings.Health.RestMaxHp", StringComparison.Ordinal)
                                  && w.Contains("its default", StringComparison.Ordinal));
    }

    // The value used is what the next save writes, beside everything else the
    // character held.
    [Fact]
    public void ProfileSave_AfterAClampedLoad_WritesTheCharacterBackWhole()
    {
        string path = WriteProfile("""
            { "Name": "Hero", "Favorites": [ { "Label": "Home", "Map": 1, "Room": 2 } ], "EventQueueLimit": 100000 }
            """);
        ProfileService profiles = TestProfiles.Service();
        profiles.Load(_bbs, "Hero");

        profiles.Save();

        CharacterProfile onFile = JsonSerializer.Deserialize<CharacterProfile>(File.ReadAllText(path), JsonStore.Options)!;
        Assert.Equal("Hero", onFile.Name);
        Assert.Equal("Home", Assert.Single(onFile.Favorites!).Label);
        Assert.Equal(100, onFile.EventQueueLimit);
    }

    // A file that isn't JSON at all still fails to load, and the client comes up on
    // the default profile. Nothing it saves from there may land on the character's
    // file: the unnamed draft is never written, so the file waits to be repaired.
    [Fact]
    public void ProfileThatWontParse_FallingBackToTheDefault_NeverWritesOverIt()
    {
        string path = WriteProfile("""{ "Name": "Hero", "Favorites": [ { "Label": "Home" """);
        byte[] before = File.ReadAllBytes(path);
        ProfileService profiles = TestProfiles.Service();

        Assert.Throws<InvalidDataException>(() => profiles.Load(_bbs, "Hero"));
        profiles.LoadDefaultProfile();
        profiles.Save();

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // Global tier: two billion days of player cleanup threw "AddDays out of range"
    // from the startup prune, and a section value at that tier reaches every read.
    [Fact]
    public void GlobalLoad_OutOfRangeValues_AreClamped_AndThePruneCopes()
    {
        string file = AppPaths.GlobalSettingsFile;
        string? saved = File.Exists(file) ? File.ReadAllText(file) : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, """
                { "PlayerCleanupDays": 2147483647, "LocalApiPort": 5, "Settings": { "Talk": { "LogMaxLines": 1e12 } } }
                """);

            SettingsService settings = new();
            ProfileService profiles = TestProfiles.Loaded();
            SettingsResolver resolver = new(settings, new BbsProfileStore(), profiles);

            Assert.Equal(3650, settings.Current.PlayerCleanupDays);
            Assert.Equal(1024, settings.Current.LocalApiPort);
            Assert.Equal(100_000, resolver.Resolve<TalkSettings>("Talk").LogMaxLines);
            Assert.Contains(WarnsAbout(file), w => w.StartsWith("Global setting PlayerCleanupDays", StringComparison.Ordinal));
            PlayerDatabase players = new(profiles, () => null);
            Assert.Equal(0, players.PurgeStale(settings.Current.PlayerCleanupDays, DateTime.UtcNow));
        }
        finally
        {
            if (saved is null) File.Delete(file);
            else File.WriteAllText(file, saved);
        }
    }

    // Whatever window a caller passes, the prune never throws.
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(800_000)]
    public void PurgeStale_AnyWindow_DoesNotThrow(int days)
    {
        PlayerDatabase players = new(TestProfiles.Loaded(), () => null);

        Assert.Equal(0, players.PurgeStale(days, DateTime.UtcNow));
        Assert.Equal(0, players.PurgeStale(days, new DateTime(2, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }
}

// Every number a settings section or file holds has a range, and the range never
// takes away a value the Settings window lets the user set.
public sealed class SettingRangeCoverageTests
{
    // The DTOs a user configures: the sections, the three files' own settings and
    // the settings objects they hold. Recorded state (stats, observations, death
    // and quest records) is held only to what its type can hold.
    private static readonly Type[] SettingsTypes = SettingsValueGuard.Sections.Values.Concat(new[]
    {
        typeof(GlobalSettings), typeof(BbsProfile), typeof(RealmProfile), typeof(NavLineStyle), typeof(GuideProgress),
        typeof(EncumbranceHopTimes), typeof(CombatSpellSlot), typeof(PvpSpellSlot), typeof(SoundCueSettings),
        typeof(BuffSlot), typeof(EquipmentSettings), typeof(CombatSpellProfile), typeof(CombatProfileSpells),
        typeof(CombatProfileParty), typeof(MonsterHitColorSettings), typeof(Models.GameData.ScheduledEvent),
        typeof(SessionStatsLayout), typeof(WindowBounds), typeof(CpPlanEntry),
    }).Distinct().ToArray();

    private static bool IsNumber(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsGenericType && t.GetGenericTypeDefinition() is var g && (g == typeof(List<>) || g == typeof(Dictionary<,>)))
            t = t.GetGenericArguments()[^1];
        return t == typeof(int) || t == typeof(long) || t == typeof(double);
    }

    [Fact]
    public void EveryNumberASettingsTypeHolds_HasARange()
    {
        List<string> missing = SettingsTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && IsNumber(p.PropertyType) && p.Name != "SchemaVersion"
                            && p.GetCustomAttribute<SettingRangeAttribute>() is null)
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        Assert.Empty(missing);
    }

    // A default outside its own range would be moved, and warned about, on every load.
    [Fact]
    public void EveryDefault_LiesInItsRange()
    {
        List<string> outside = new();
        foreach (Type type in typeof(SettingRangeAttribute).Assembly.GetTypes())
        {
            PropertyInfo[] ranged = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<SettingRangeAttribute>() is not null).ToArray();
            if (ranged.Length == 0 || type.GetConstructor(Type.EmptyTypes) is null) continue;
            object instance = Activator.CreateInstance(type)!;
            foreach (PropertyInfo p in ranged)
            {
                SettingRangeAttribute range = p.GetCustomAttribute<SettingRangeAttribute>()!;
                Assert.True(range.Min <= range.Max, $"{type.Name}.{p.Name}");
                if (p.GetValue(instance) is not { } value || value is System.Collections.IEnumerable) continue;
                double d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                if (d < range.Min || d > range.Max) outside.Add($"{type.Name}.{p.Name} = {d}");
            }
        }

        Assert.Empty(outside);
    }

    // Each NumericUpDown in the Settings windows bound to a field of the same name:
    // whatever it lets the user enter, the load keeps.
    [Fact]
    public void EveryRange_HoldsWhatTheSettingsWindowAllows()
    {
        string views = Path.Combine(RepoRoot(), "Views");
        IEnumerable<string> files = Directory.GetFiles(Path.Combine(views, "Settings"), "*.axaml")
            .Append(Path.Combine(views, "AddBuffDialog.axaml"))
            .Append(Path.Combine(views, "CharacterWorkshop", "EquipmentSectionView.axaml"));
        Dictionary<string, List<(string Owner, SettingRangeAttribute Range)>> ranged = new(StringComparer.Ordinal);
        foreach (Type type in typeof(SettingRangeAttribute).Assembly.GetTypes())
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (p.GetCustomAttribute<SettingRangeAttribute>() is { } r)
                    (ranged.TryGetValue(p.Name, out var list) ? list : ranged[p.Name] = new()).Add(($"{type.Name}.{p.Name}", r));

        Regex control = new(@"<NumericUpDown\b(?<attrs>[^>]*)>", RegexOptions.Singleline);
        int compared = 0;
        List<string> narrower = new();
        foreach (string file in files)
            foreach (Match m in control.Matches(File.ReadAllText(file)))
            {
                string attrs = m.Groups["attrs"].Value;
                if (Regex.Match(attrs, @"Value=""\{Binding (\w+)") is not { Success: true } bound) continue;
                if (!ranged.TryGetValue(bound.Groups[1].Value, out var owners)) continue;
                double? min = Literal(attrs, "Minimum"), max = Literal(attrs, "Maximum");
                foreach ((string owner, SettingRangeAttribute range) in owners)
                {
                    compared++;
                    if (min is { } lo && lo < range.Min) narrower.Add($"{owner}: window allows {lo}, range starts {range.Min}");
                    if (max is { } hi && hi > range.Max) narrower.Add($"{owner}: window allows {hi}, range ends {range.Max}");
                }
            }

        Assert.Empty(narrower);
        Assert.True(compared > 50, $"only {compared} controls matched a ranged field");
    }

    private static double? Literal(string attrs, string name) =>
        Regex.Match(attrs, name + @"=""(-?[0-9.]+)""") is { Success: true } m
            ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(here)!)!;
}
