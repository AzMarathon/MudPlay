using System.ComponentModel;
using MudPlay.Game.Calculators;
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
// depends on changes and the reads are field reads. The age check on a last tick is
// the one thing done per read: a tick that stopped coming leaves nothing to fire on.
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
    private readonly HpRegenExpectationSource _hpExpected;
    private readonly ManaRegenReroller _reroller;
    private readonly Func<IReadOnlyList<QuestBonus>?> _questBonuses;
    // The level-scaled range of the mana-regen roll spell while it is up; null when
    // none is configured or it isn't up.
    private readonly Func<(int Min, int Max)?> _rollSpellUp;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _clock;
    // Runs a refresh on the UI thread: inventory changes can arrive off it.
    private readonly Action<Action> _post;
    private int _refreshQueued;

    private sealed record Kept(DateTimeOffset At, int Delta, PlayerPosition Position, string Text, string TextWithPosture);

    private Kept? _hpLast;
    private Kept? _maLast;

    public RegenReadout(RegenTracker tracker, PlayerState state, PlayerStats stats, InventoryManager inventory,
        GameDataCache gameData, HpRegenExpectationSource hpExpected, ManaRegenReroller reroller,
        Func<IReadOnlyList<QuestBonus>?> questBonuses, Func<(int Min, int Max)?>? rollSpellUp = null,
        LogService? log = null, Func<DateTimeOffset>? clock = null, Action<Action>? post = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
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
        _reroller.RollCastOrRead += QueueRefresh;
        QueueRefresh();
    }

    // ----- what the bar shows ---------------------------------------------------

    public string HpLastText => Shown(_hpLast);
    public string MaLastText => Shown(_maLast);
    public string HpExpectedText { get; private set; } = string.Empty;
    public string MaExpectedText { get; private set; } = string.Empty;

    // What each expected text was worked from, for the bug report and the log.
    public string HpExpectedInputs { get; private set; } = "(not worked out yet)";
    public string MaExpectedInputs { get; private set; } = "(not worked out yet)";

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
        // A mana pass is also when a mana-regen roll spell's expiry shows: refresh.
        QueueRefresh();
        if (_state.MaxMa > 0 && _state.Ma >= _state.MaxMa) return;
        _maLast = Keep(_state.ManaType == ManaType.Kai ? "KAI" : "MA", sample);
    }

    private static Kept Keep(string pool, RegenSample sample)
    {
        string text = $"{pool} +{sample.Delta}";
        return new Kept(sample.Timestamp, sample.Delta, sample.Position, text, $"{text} ({Word(sample.Position)})");
    }

    // Disconnect, the game left, a death, a profile load: the ticks kept are another
    // session's or another character's.
    public void Forget(string why)
    {
        if (_hpLast is null && _maLast is null) return;
        _hpLast = _maLast = null;
        _log?.Debug(LogCategory, $"regen readout: last ticks forgotten ({why})");
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

    // Quest bonuses aren't watched: a profile load is when they change, and the
    // client calls this then.
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
        if (ma != MaExpectedText) _log?.Debug(LogCategory, $"mana regen expected: {Logged(ma)} — {maInputs}");
        (HpExpectedText, HpExpectedInputs, MaExpectedText, MaExpectedInputs) = (hp, hpInputs, ma, maInputs);
    }

    private static string Logged(string text) => text.Length == 0 ? "(nothing shown)" : text;

    private (string Text, string Inputs) WorkOutHp(RealmType realm, PlayerPosition posture)
    {
        int level = _stats.Level, health = _stats.Health;
        if (level <= 0 || health <= 0) return (string.Empty, "no `stat` read yet");
        int percent = _hpExpected.Percent;
        string inputs = $"level {level}, Health {health}, HP regen {percent}% (gear, race, class, quests), {Word(posture)}, {realm}";
        if (realm == RealmType.ParaMud)
        {
            // Standing pays a third every 10 s; a rest a gain every 5 s, three small
            // then three whole. Meditating pays HP at the standing rate.
            var g = CharacterCalculator.ParadigmHpRegenGains(level, health, percent);
            string thirds = $"+{g.Third}/+{g.Third}/+{g.LastThird}";
            return posture == PlayerPosition.Resting
                ? ($"HP {thirds} then +{g.Full}/+{g.Full}/+{g.Full} per 5s", inputs)
                : ($"HP {thirds} per 10s", inputs);
        }
        int idle = CharacterCalculator.CalcHpRegen(level, health, percent, isResting: false, realm);
        if (posture != PlayerPosition.Resting) return ($"HP +{idle}/tick", inputs);
        // The 30 s pass keeps paying while a rest tick comes every 21 s.
        int rest = CharacterCalculator.CalcHpRegen(level, health, percent, isResting: true, realm);
        return ($"HP +{rest}/rest tick (+{idle}/30s)", inputs);
    }

    private (string Text, string Inputs) WorkOutMana(RealmType realm, PlayerPosition posture)
    {
        int level = _stats.Level;
        if (level <= 0) return (string.Empty, "no `stat` read yet");
        StatContext cls = StatContext.Resolve(_gameData, _stats.Class, _stats.Race, realm);
        if (cls.MageryType == 0) return (string.Empty, $"{_stats.Class} has no mana");
        int worn = CharacterCalculator.CharacterTotals(
            _stats, _inventory.Snapshot.EquippedItems, _gameData, _questBonuses()).MpRegenPercent;
        int Tick(int percent, bool meditating) => CharacterCalculator.CalcManaRegen(level, _stats.Intellect,
            _stats.Willpower, _stats.Charm, cls.MageryType, cls.MageryLevel, percent, meditating, realm);
        string stats = $"level {level}, magery {cls.MageryType} tier {cls.MageryLevel}, "
            + $"INT {_stats.Intellect} WIL {_stats.Willpower} CHA {_stats.Charm}, ManaRgn {worn}% (gear, race, class, quests)";

        // Kai is a fixed rate the roll and meditating don't enter.
        if (cls.MageryType == KaiMagery)
            return ($"KAI +{Tick(worn, false)}/tick", $"{stats}, {Word(posture)}, {realm}");

        // A roll spell's roll adds to ManaRgn%: once read, it pins the tick; until
        // then the tick is a range over what the roll can be.
        int low = Tick(worn, false), high = low;
        string rollNote = string.Empty, rollInputs = "no roll spell up";
        if (_rollSpellUp() is { } range)
        {
            if (_reroller.CurrentRoll is { } roll)
            {
                low = high = Tick(worn + roll, false);
                rollInputs = $"roll {roll}";
                // Stock's roll is the top of a band read off the tick: not a number to show.
                if (realm == RealmType.ParaMud) rollNote = $" (roll {roll})";
            }
            else
            {
                int atMin = Tick(worn + range.Min, false), atMax = Tick(worn + range.Max, false);
                (low, high) = (Math.Min(atMin, atMax), Math.Max(atMin, atMax));
                rollInputs = $"roll not read yet, {range.Min}..{range.Max}";
            }
        }
        string passive = low == high ? $"+{low}" : $"+{low}–{high}";
        string inputs = $"{stats}, {rollInputs}, {Word(posture)}, {realm}";
        // Meditating pays the base without ManaRgn% every 15 s, and the 30 s pass
        // keeps paying beside it.
        return posture == PlayerPosition.Meditating
            ? ($"MA +{Tick(0, true)}/meditate tick ({passive}/30s)", inputs)
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
            : $"+{kept.Delta}, {Word(kept.Position)}, {(now - kept.At).TotalSeconds:0} s ago"
              + (now - kept.At > LastTickShownFor ? " (too old: the bar shows nothing)" : string.Empty);
        return $"Regen readout (status bar), {Word(_state.Position)} now:\n"
            + $"- HP last tick: {Last(_hpLast)}\n"
            + $"- HP expected: {Logged(HpExpectedText)} — {HpExpectedInputs}\n"
            + $"- Mana last tick: {Last(_maLast)}\n"
            + $"- Mana expected: {Logged(MaExpectedText)} — {MaExpectedInputs}";
    }

    public void Dispose()
    {
        _tracker.HpTickObserved -= OnHpTick;
        _tracker.MaTickObserved -= OnMaTick;
        _state.PropertyChanged -= OnStateChanged;
        _stats.PropertyChanged -= OnStatsChanged;
        _inventory.Changed -= QueueRefresh;
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
        _reroller.RollCastOrRead -= QueueRefresh;
    }
}
