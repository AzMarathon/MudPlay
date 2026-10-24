using System.ComponentModel;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Quests;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game;

// The status bar's regen items: what the last HP and mana regen tick gave, and what
// the character's stats say a tick gives in the present posture (GAME_MECHANICS "Rest
// and meditate tick timing", "Mana regeneration & the ManaRgn breakpoints").
//
// The bar reads these twice a second, so every text is worked out when something it
// depends on changes and the reads are field reads. Two clock compares are done per
// read, for what has no event to fire on: a last tick that stopped coming, and a
// mana-regen roll spell running out.
public sealed class RegenReadout : IDisposable
{
    public const string LogCategory = "Regen";

    // A last tick older than this is blanked: the pool has stopped ticking (full, or
    // the character gone quiet) and the number no longer describes anything.
    public static readonly TimeSpan LastTickShownFor = TimeSpan.FromMinutes(2);

    private const int KaiMagery = 5;

    private readonly RegenTracker _tracker;
    private readonly PlayerState _state;
    private readonly PlayerStats _stats;
    private readonly InventoryManager _inventory;
    private readonly GameDataCache _gameData;
    private readonly ProfileService _profile;
    private readonly InGameCapture _inGame;
    private readonly DeathLineWatcher _deaths;
    private readonly HpRegenExpectationSource _hpExpected;
    private readonly ManaRegenReroller _reroller;
    private readonly Func<IReadOnlyList<QuestBonus>?> _questBonuses;
    // The mana-regen roll spell's level-scaled range and when its timer runs out,
    // while it is up; null when none is configured or it isn't up.
    private readonly Func<(int Min, int Max, DateTimeOffset Until)?> _rollSpellUp;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _clock;
    // Runs a refresh on the UI thread: inventory changes can arrive off it.
    private readonly Action<Action> _post;
    private int _refreshQueued;

    private sealed record Kept(DateTimeOffset At, int Delta, PlayerPosition Position, string Text, string TextWithPosture);

    private Kept? _hpLast;
    private Kept? _maLast;
    private string _maExpectedText = string.Empty;
    // When the roll spell counted in the mana text runs out.
    private DateTimeOffset? _rollUntil;
    // What each expected text was worked from, for the bug report and the log.
    private string _hpExpectedInputs = "(not worked out yet)";
    private string _maExpectedInputs = "(not worked out yet)";

    public RegenReadout(RegenTracker tracker, PlayerState state, PlayerStats stats, InventoryManager inventory,
        GameDataCache gameData, ProfileService profile, InGameCapture inGame, DeathLineWatcher deaths,
        HpRegenExpectationSource hpExpected, ManaRegenReroller reroller, Func<IReadOnlyList<QuestBonus>?> questBonuses,
        Func<(int Min, int Max, DateTimeOffset Until)?>? rollSpellUp = null,
        LogService? log = null, Func<DateTimeOffset>? clock = null, Action<Action>? post = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _inGame = inGame ?? throw new ArgumentNullException(nameof(inGame));
        _deaths = deaths ?? throw new ArgumentNullException(nameof(deaths));
        _hpExpected = hpExpected ?? throw new ArgumentNullException(nameof(hpExpected));
        _reroller = reroller ?? throw new ArgumentNullException(nameof(reroller));
        _questBonuses = questBonuses ?? throw new ArgumentNullException(nameof(questBonuses));
        _rollSpellUp = rollSpellUp ?? (() => null);
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _post = post ?? (a => a());

        _tracker.HpTickObserved += OnHpTick;
        _tracker.MaTickObserved += OnMaTick;
        _state.PropertyChanged += OnStateChanged;
        _stats.PropertyChanged += OnStatsChanged;
        _inventory.Changed += QueueRefresh;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _inGame.InGameChanged += OnInGameChanged;
        _deaths.PlayerDied += OnPlayerDied;
        _reroller.RollCastOrRead += QueueRefresh;
        QueueRefresh();
    }

    // ----- what the bar shows ---------------------------------------------------

    public string HpLastText => Shown(_hpLast);
    public string MaLastText => Shown(_maLast);
    public string HpExpectedText { get; private set; } = string.Empty;

    public string MaExpectedText
    {
        get
        {
            if (_rollUntil is { } until && _clock() >= until)
            {
                _rollUntil = null;
                QueueRefresh();
            }
            return _maExpectedText;
        }
    }

    private string Shown(Kept? kept)
    {
        if (kept is null || _clock() - kept.At > LastTickShownFor) return string.Empty;
        return kept.Position == _state.Position ? kept.Text : kept.TextWithPosture;
    }

    // ----- last tick ------------------------------------------------------------

    // A tick that filled the pool was cut short at the max, so its size isn't a tick's.
    private void OnHpTick(RegenSample sample)
    {
        if (_state.MaxHp > 0 && _state.Hp >= _state.MaxHp) return;
        _hpLast = Keep("HP", sample);
    }

    private void OnMaTick(RegenSample sample)
    {
        if (_state.MaxMa > 0 && _state.Ma >= _state.MaxMa) return;
        _maLast = Keep(_state.ManaType == ManaType.Kai ? "KAI" : "MA", sample);
    }

    private static Kept Keep(string pool, RegenSample sample)
    {
        string text = $"{pool} {Signed(sample.Delta)}";
        return new Kept(sample.Timestamp, sample.Delta, sample.Position, text, $"{text} ({Word(sample.Position)})");
    }

    // The ticks kept are another session's or another character's.
    private void Forget(string why)
    {
        if (_hpLast is null && _maLast is null) return;
        _hpLast = _maLast = null;
        _log?.Debug(LogCategory, $"regen readout: last ticks forgotten ({why})");
    }

    private void OnInGameChanged(bool inGame)
    {
        if (!inGame) Forget("out of the game");
    }

    // A death wipes the character's effects, a roll spell's among them.
    private void OnPlayerDied()
    {
        Forget("the character died");
        QueueRefresh();
    }

    private void OnProfileLoaded(Models.Profile.CharacterProfile profile)
    {
        Forget("a profile was loaded");
        QueueRefresh();
    }

    // ----- expected -------------------------------------------------------------

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerState.Position)) QueueRefresh();
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerStats.Level) or nameof(PlayerStats.Health) or nameof(PlayerStats.Class)
            or nameof(PlayerStats.Race) or nameof(PlayerStats.Intellect) or nameof(PlayerStats.Willpower)
            or nameof(PlayerStats.Charm) or null or "")
            QueueRefresh();
    }

    private void OnActiveSetChanged(string? setName)
    {
        Forget("the game data set changed");
        QueueRefresh();
    }

    // Also called by the client when a quest sync marks quests complete: their
    // bonuses enter both regen percents and aren't watched here.
    public void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _post(Refresh);
    }

    private void Refresh()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        RealmType realm = _gameData.ActiveRealm;
        PlayerPosition posture = _state.Position;
        (string hp, string hpInputs) = WorkOutHp(realm, posture);
        (string ma, string maInputs) = WorkOutMana(realm, posture);
        if (hp != HpExpectedText) _log?.Debug(LogCategory, $"HP regen expected: {Logged(hp)} — {hpInputs}");
        if (ma != _maExpectedText) _log?.Debug(LogCategory, $"mana regen expected: {Logged(ma)} — {maInputs}");
        (HpExpectedText, _hpExpectedInputs, _maExpectedText, _maExpectedInputs) = (hp, hpInputs, ma, maInputs);
    }

    private static string Logged(string text) => text.Length == 0 ? "(nothing shown)" : text;

    // A regen percent below zero can take a gain to nothing or under it.
    private static string Signed(int amount) => amount < 0 ? $"{amount}" : $"+{amount}";

    private (string Text, string Inputs) WorkOutHp(RealmType realm, PlayerPosition posture)
    {
        int level = _stats.Level, health = _stats.Health;
        if (level <= 0 || health <= 0) return (string.Empty, "no `stat` read yet");
        int percent = _hpExpected.Percent;
        string inputs = $"level {level}, Health {health}, HP regen {percent}% (gear, race, class, quests), {Word(posture)}, {realm}";
        if (realm == RealmType.ParaMud)
        {
            // Standing pays a third every 10 s; a rest a gain every 5 s, three small
            // then three whole. Meditating is shown at the standing rate.
            var g = CharacterCalculator.ParadigmHpRegenGains(level, health, percent);
            string thirds = $"{Signed(g.Third)}/{Signed(g.Third)}/{Signed(g.LastThird)}";
            string full = Signed(g.Full);
            return posture == PlayerPosition.Resting
                ? ($"HP {thirds} then {full}/{full}/{full} per 5s", inputs)
                : ($"HP {thirds} per 10s", inputs);
        }
        int idle = CharacterCalculator.CalcHpRegen(level, health, percent, isResting: false, realm);
        if (posture != PlayerPosition.Resting) return ($"HP {Signed(idle)}/tick", inputs);
        // The 30 s pass keeps paying while a rest tick comes every 21 s.
        int rest = CharacterCalculator.CalcHpRegen(level, health, percent, isResting: true, realm);
        return ($"HP {Signed(rest)}/rest tick ({Signed(idle)}/30s)", inputs);
    }

    private (string Text, string Inputs) WorkOutMana(RealmType realm, PlayerPosition posture)
    {
        _rollUntil = null;
        int level = _stats.Level;
        if (level <= 0) return (string.Empty, "no `stat` read yet");
        StatContext cls = StatContext.Resolve(_gameData, _stats.Class, _stats.Race, realm);
        if (cls.MageryType == 0) return (string.Empty, $"{_stats.Class} has no mana");

        if (cls.MageryType == KaiMagery)
        {
            // Kai regenerates 1 a tick, and 1 more for every 100 ManaRgn the mystic
            // wears (user, 2026-10-24): gear only, never below nothing.
            int wornGear = CharacterCalculator.AggregateEquipmentStats(
                _inventory.Snapshot.EquippedItems, _gameData).Totals.MpRegenPercent;
            int kai = Math.Max(0, 1 + (int)Math.Floor(wornGear / 100.0));
            return ($"KAI +{kai}/tick", $"level {level}, worn ManaRgn {wornGear}, {Word(posture)}, {realm}");
        }

        int worn = CharacterCalculator.CharacterTotals(
            _stats, _inventory.Snapshot.EquippedItems, _gameData, _questBonuses()).MpRegenPercent;
        int Tick(int percent, bool meditating) => CharacterCalculator.CalcManaRegen(level, _stats.Intellect,
            _stats.Willpower, _stats.Charm, cls.MageryType, cls.MageryLevel, percent, meditating, realm);
        string stats = $"level {level}, magery {cls.MageryType} tier {cls.MageryLevel}, "
            + $"INT {_stats.Intellect} WIL {_stats.Willpower} CHA {_stats.Charm}, ManaRgn {worn}% (gear, race, class, quests)";

        // A roll spell's roll adds to ManaRgn%: once read, it pins the tick; until
        // then the tick is a range over what the roll can be.
        int low = Tick(worn, false), high = low;
        string rollNote = string.Empty, rollInputs = "no roll spell up";
        if (_rollSpellUp() is { } spell)
        {
            _rollUntil = spell.Until;
            if (_reroller.CurrentRollTick is { } readTick)
            {
                // Stock reads the roll off a natural tick, with the stats and gear of
                // that moment: the tick itself is what the roll pays.
                low = high = readTick;
                rollInputs = $"roll read off a {readTick} tick";
            }
            else if (_reroller.CurrentRoll is { } roll)
            {
                low = high = Tick(worn + roll, false);
                rollInputs = $"roll {roll}";
                rollNote = $" (roll {roll})";
            }
            else
            {
                int atMin = Tick(worn + spell.Min, false), atMax = Tick(worn + spell.Max, false);
                (low, high) = (Math.Min(atMin, atMax), Math.Max(atMin, atMax));
                rollInputs = $"roll not read yet, {spell.Min}..{spell.Max}";
            }
        }
        string passive = low == high ? Signed(low) : $"{Signed(low)}–{(low < 0 ? Signed(high) : $"{high}")}";
        string inputs = $"{stats}, {rollInputs}, {Word(posture)}, {realm}";
        // Meditating pays the base without ManaRgn% every 15 s, and the 30 s pass
        // keeps paying beside it.
        return posture == PlayerPosition.Meditating
            ? ($"MA {Signed(Tick(0, true))}/meditate tick ({passive}/30s)", inputs)
            : ($"MA {passive}/tick{rollNote}", inputs);
    }

    private static string Word(PlayerPosition position) => position switch
    {
        PlayerPosition.Resting => "resting",
        PlayerPosition.Meditating => "meditating",
        _ => "standing",
    };

    // ----- bug report -----------------------------------------------------------

    public string Describe()
    {
        DateTimeOffset now = _clock();
        string Last(Kept? kept) => kept is null
            ? "(none kept)"
            : $"{Signed(kept.Delta)}, {Word(kept.Position)}, {(now - kept.At).TotalSeconds:0} s ago"
              + (now - kept.At > LastTickShownFor ? " (too old: the bar shows nothing)" : string.Empty);
        return $"Regen readout (status bar), {Word(_state.Position)} now:\n"
            + $"- HP last tick: {Last(_hpLast)}\n"
            + $"- HP expected: {Logged(HpExpectedText)} — {_hpExpectedInputs}\n"
            + $"- Mana last tick: {Last(_maLast)}\n"
            + $"- Mana expected: {Logged(_maExpectedText)} — {_maExpectedInputs}"
            + (_rollUntil is { } until ? $" (roll spell up until {until:HH:mm:ss})" : string.Empty);
    }

    public void Dispose()
    {
        _tracker.HpTickObserved -= OnHpTick;
        _tracker.MaTickObserved -= OnMaTick;
        _state.PropertyChanged -= OnStateChanged;
        _stats.PropertyChanged -= OnStatsChanged;
        _inventory.Changed -= QueueRefresh;
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _inGame.InGameChanged -= OnInGameChanged;
        _deaths.PlayerDied -= OnPlayerDied;
        _reroller.RollCastOrRead -= QueueRefresh;
    }
}
