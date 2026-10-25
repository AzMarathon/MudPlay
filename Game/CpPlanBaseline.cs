using System.ComponentModel;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game;

// The inputs the CP plan grid is worked from, kept steady while a buff is up.
//
// A `stat` screen read with a buff or curse on a stat can't always be worked back to
// the trained stats (CharacterPlanContext.BaselineTrusted): an effect line nothing on
// record explains, any marked stat on Stock, a screen still printing. Worked from the
// numbers such a screen shows, the grid's CP Left moved every time the buff came and
// went (report paradigm-20261025-010655). So such a reading leaves the grid on the
// last reading of this character that could be worked back, with that reading's CP
// and level, which keeps the three in step: a held baseline beside the latest CP
// would price the old stats against CP already spent on new ones. With no such
// reading yet this session, the latest figures stand.
//
// Kept for the session, whether or not the Player Workshop is open, so opening it
// with a buff up still finds the last clean reading.
public sealed class CpPlanBaseline : IDisposable
{
    private const string LogSource = "CpPlan";

    // The PlayerStats fields a plan reading is made of. The rest (exp, vitals) move
    // all the time and change nothing here.
    private static readonly HashSet<string> ReadingFields = new(StringComparer.Ordinal)
    {
        nameof(PlayerStats.Name), nameof(PlayerStats.Race), nameof(PlayerStats.Level),
        nameof(PlayerStats.Cp), nameof(PlayerStats.CpThroughLevel),
        nameof(PlayerStats.Strength), nameof(PlayerStats.Intellect), nameof(PlayerStats.Willpower),
        nameof(PlayerStats.Agility), nameof(PlayerStats.Health), nameof(PlayerStats.Charm),
        nameof(PlayerStats.ModifiedStats), nameof(PlayerStats.ModifiedMarksRead), nameof(PlayerStats.ActiveEffects),
    };

    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    private readonly ListedEffectCatalog _listedEffects;
    private readonly ProfileService _profile;
    private readonly LogService? _log;

    private sealed record Reading(string Name, CpPlanInputs Inputs);
    private Reading? _lastTrusted;
    private string? _lastLogged;

    public CpPlanBaseline(PlayerStats stats, GameDataCache gameData, InventoryManager inventory,
                          ListedEffectCatalog listedEffects, ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(listedEffects);
        ArgumentNullException.ThrowIfNull(profile);
        _stats = stats;
        _gameData = gameData;
        _inventory = inventory;
        _listedEffects = listedEffects;
        _profile = profile;
        _log = log;
        _stats.PropertyChanged += OnStatsChanged;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        _profile.ProfileClosed += Forget;
        _profile.ProfileLoaded += OnProfileLoaded;
    }

    // Resolve the live plan context and the inputs the grid is worked from, keeping
    // a trusted reading for later.
    public CpPlanInputs Resolve(out CharacterPlanContext ctx)
    {
        CpPlanInputs used = Choose(out ctx);
        if (!ctx.HasCharacter) return used;
        if (ctx.BaselineTrusted) _lastTrusted = new Reading(_stats.Name, used);
        LogIfChanged(used, ctx);
        return used;
    }

    // The same answer as Resolve, with nothing kept or logged: a bug report can be
    // captured off the UI thread, by the crash reporter.
    public CpPlanInputs Peek(out CharacterPlanContext ctx) => Choose(out ctx);

    private CpPlanInputs Choose(out CharacterPlanContext ctx)
    {
        ctx = CharacterPlanContext.Resolve(_stats, _gameData, _inventory, _listedEffects);
        var live = new CpPlanInputs(ctx.Baseline, _stats.Cp, _stats.CpThroughLevel, Held: false);
        if (!ctx.HasCharacter || ctx.BaselineTrusted) return live;
        return _lastTrusted is { } last && last.Name == _stats.Name
            ? last.Inputs with { Held = true }
            : live;
    }

    private void LogIfChanged(CpPlanInputs used, CharacterPlanContext ctx)
    {
        // CpPlanEntry compares by reference, so the inputs are compared by what they say.
        string said = (used.Held ? "held " : "live ") + used.Describe();
        if (_lastLogged == said) return;
        _lastLogged = said;
        _log?.Debug(LogSource, used.Held
            ? $"CP plan baseline held at the last `stat` that could be worked back ({used.Describe()}); "
              + $"the latest reading is {ctx.Reading.State}, marked {ctx.Reading.Modified}, "
              + $"shown STR {_stats.Strength} INT {_stats.Intellect} WIL {_stats.Willpower} AGL {_stats.Agility} "
              + $"HEA {_stats.Health} CHM {_stats.Charm}."
            : $"CP plan baseline from the latest `stat` ({ctx.Reading.State}, "
              + (ctx.BaselineTrusted ? "trusted" : "not trusted, nothing earlier to hold") + $"): {used.Describe()}.");
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && ReadingFields.Contains(name)) Resolve(out _);
    }

    // Another set can change the realm and the race bounds the baseline was floored
    // at, and another profile is another character: nothing held crosses either.
    private void OnActiveSetChanged(string? _) => Forget();
    private void OnProfileLoaded(Models.Profile.CharacterProfile _) => Forget();

    private void Forget()
    {
        _lastTrusted = null;
        _lastLogged = null;
    }

    public void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
        _profile.ProfileClosed -= Forget;
        _profile.ProfileLoaded -= OnProfileLoaded;
    }
}
