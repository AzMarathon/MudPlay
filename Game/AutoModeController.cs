using System.Text.Json;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game;

// The master switch for the client's automation. Off means nothing automatic
// acts: the eleven auto toggles are switched off (after remembering which were
// on), and every system with no toggle of its own (remote commands, triggers,
// events, polls, holds, trips, hang-ups, …) asks Blocks before it acts. On gives
// the remembered toggles back. Shared by the "Auto-All" toolbar button /
// Action-menu item and the `@auto-all` remote command, so both drive the same
// state.
//
// The switch is the ONE "all autos off" signal. Unticking the eleven toggles one
// by one is not it: some automatic systems have no toggle, so only the switch
// can say the user wants all of them quiet (user, 2026-10-09).
//
// The state is per-session and per-character: cleared on profile load, so a
// freshly loaded character never inherits the previous one's switch or toggles.
public sealed class AutoModeController
{
    private const string TabKey = "General";
    private const string LogCategory = "AutoMode";

    // The toggled engines, in stable order.
    private static readonly (string Name,
                             Func<AutoActionDefaults, bool> Get,
                             Action<AutoActionDefaults, bool> Set)[] Wired =
    {
        ("Combat",    d => d.AutoCombat,   (d, v) => d.AutoCombat   = v),
        ("Nuke",      d => d.AutoNuke,     (d, v) => d.AutoNuke     = v),
        ("Heal",      d => d.AutoHeal,     (d, v) => d.AutoHeal     = v),
        ("Rest",      d => d.AutoRest,     (d, v) => d.AutoRest     = v),
        ("Bless",     d => d.AutoBless,    (d, v) => d.AutoBless    = v),
        ("Light",     d => d.AutoLight,    (d, v) => d.AutoLight    = v),
        ("Get Items", d => d.AutoGetItems, (d, v) => d.AutoGetItems = v),
        ("Get Cash",  d => d.AutoGetCash,  (d, v) => d.AutoGetCash  = v),
        ("Sneak",     d => d.AutoSneak,    (d, v) => d.AutoSneak    = v),
        ("Hide",      d => d.AutoHide,     (d, v) => d.AutoHide     = v),
        ("Search",    d => d.AutoSearch,   (d, v) => d.AutoSearch   = v),
    };

    private readonly ProfileService _profile;
    private readonly LogService? _log;

    // Which toggles were on when the switch went off. null while the switch is on.
    private bool[]? _snapshot;

    private bool _killEngaged;

    // Guards the skip counters: a gated system may ask from a timer thread.
    private readonly object _skipLock = new();
    private readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);

    // Fires when the switch changes: true as it goes off, false as it comes back
    // on. Also fired (false) by ResetSnapshot when a profile load clears a switch
    // that was off, so nothing stays held with no switch left to release it.
    public event Action<bool>? KillSwitchToggled;

    // Asked as the switch goes off: what was running that it stops or holds, for
    // the one log line that records the change. AppServices supplies it once the
    // movement engines exist.
    public Func<string?>? DescribeInFlight { get; set; }

    public AutoModeController(ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _log = log;
    }

    // True when every toggle is off. Says nothing about the master switch: a
    // character played by hand has every toggle off with the switch still on.
    public bool AllWiredOff
    {
        get
        {
            if (_profile.Current is not { } profile) return true;
            AutoActionDefaults am = ReadGeneral(profile).AutoMode;
            foreach ((_, Func<AutoActionDefaults, bool> get, _) in Wired)
                if (get(am)) return false;
            return true;
        }
    }

    // True while the master switch is off.
    public bool KillSwitchEngaged => _killEngaged;

    // The gate every untoggled automatic system asks before acting: true means
    // the master switch is off and the caller must do nothing. Each skip is
    // counted under the system's name for the bug report and the switch-on log
    // line. A skip with a detail (which command, which trigger) is logged every
    // time; one without is a poll, logged the first time only so a ticking probe
    // can't flood the log.
    public bool Blocks(string system, string? detail = null)
    {
        if (!_killEngaged) return false;
        int count;
        lock (_skipLock)
        {
            _skipped.TryGetValue(system, out count);
            _skipped[system] = ++count;
        }
        if (detail is not null)
            _log?.Log(LogSeverity.Debug, LogCategory, $"{system}: skipped {detail} — master switch off");
        else if (count == 1)
            _log?.Log(LogSeverity.Debug, LogCategory, $"{system}: skipped — master switch off (further skips counted, not logged)");
        return true;
    }

    // How many times each system has skipped since the switch last went off.
    public IReadOnlyDictionary<string, int> SkippedSinceOff
    {
        get { lock (_skipLock) { return new Dictionary<string, int>(_skipped, StringComparer.Ordinal); } }
    }

    // Clear the switch and its remembered toggles. Called on profile load so a
    // newly loaded character starts with the switch on.
    public void ResetSnapshot()
    {
        bool wasEngaged = _killEngaged;
        _snapshot = null;
        _killEngaged = false;
        if (wasEngaged) KillSwitchToggled?.Invoke(false);
    }

    // The button's press: off when on, on when off.
    public void ToggleAll(string by = "user")
    {
        if (_killEngaged) TurnOn(by);
        else TurnOff(by);
    }

    // Switch off: remember the toggles, clear them, and hold everything else. It
    // engages with every toggle already off too, since the toggles don't cover
    // every automatic system. No-op when already off.
    public void TurnOff(string by = "user")
    {
        if (_killEngaged) return;
        if (_profile.Current is not { } profile) return;

        GeneralSettings general = ReadGeneral(profile);
        AutoActionDefaults am = general.AutoMode;
        string inFlight = DescribeInFlight?.Invoke() ?? string.Empty;

        _snapshot = new bool[Wired.Length];
        List<string> wereOn = new();
        for (int i = 0; i < Wired.Length; i++)
        {
            _snapshot[i] = Wired[i].Get(am);
            if (_snapshot[i]) wereOn.Add(Wired[i].Name);
            Wired[i].Set(am, false);
        }
        lock (_skipLock) { _skipped.Clear(); }
        _killEngaged = true;
        if (wereOn.Count > 0) WriteGeneral(profile, general);

        _log?.Log(LogSeverity.Info, LogCategory,
            $"Master switch OFF ({by}). Toggles switched off: {(wereOn.Count > 0 ? string.Join(", ", wereOn) : "none were on")}."
            + (inFlight.Length > 0 ? $" {inFlight}" : string.Empty));
        KillSwitchToggled?.Invoke(true);
    }

    // Switch on. Coming back from off, the remembered toggles return, along with
    // any the user ticked by hand meanwhile. When that leaves nothing on (the
    // switch went off with every toggle already off, or it was never off and the
    // toggles are all off), the character's base modes are switched on instead, so
    // an `@auto-all on` always starts the autos the user has set up (user,
    // 2026-10-09). With the switch already on and a toggle on, nothing changes.
    public void TurnOn(string by = "user")
    {
        if (_profile.Current is not { } profile) return;

        GeneralSettings general = ReadGeneral(profile);
        AutoActionDefaults am = general.AutoMode;
        bool wasEngaged = _killEngaged;

        bool changed = false;
        if (_snapshot is { } snap)
        {
            for (int i = 0; i < Wired.Length && i < snap.Length; i++)
            {
                if (!snap[i] || Wired[i].Get(am)) continue;
                Wired[i].Set(am, true);
                changed = true;
            }
        }

        bool anyOn = false;
        foreach ((_, Func<AutoActionDefaults, bool> get, _) in Wired)
            if (get(am)) { anyOn = true; break; }

        string source = "remembered toggles";
        if (!anyOn && general.AutoModeBase is { } baseModes)
        {
            general.AutoMode = am = baseModes.Clone();
            changed = true;
            source = "base modes";
        }

        _snapshot = null;
        _killEngaged = false;
        if (changed) WriteGeneral(profile, general);

        List<string> nowOn = new();
        foreach ((string name, Func<AutoActionDefaults, bool> get, _) in Wired)
            if (get(am)) nowOn.Add(name);

        string on = nowOn.Count > 0 ? string.Join(", ", nowOn) : "none";
        if (!wasEngaged)
        {
            if (changed)
                _log?.Log(LogSeverity.Info, LogCategory,
                    $"Auto-All on ({by}) with the master switch already on and no toggle on: base modes switched on: {on}.");
            return;
        }

        _log?.Log(LogSeverity.Info, LogCategory,
            $"Master switch ON ({by}). Toggles on ({source}): {on}. Skipped while off: {DescribeSkipped()}.");
        KillSwitchToggled?.Invoke(false);
    }

    // "Remote commands 3, Triggers 12" — the skip counters as one phrase.
    public string DescribeSkipped()
    {
        IReadOnlyDictionary<string, int> skipped = SkippedSinceOff;
        if (skipped.Count == 0) return "nothing";
        return string.Join(", ", skipped.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                        .Select(kv => $"{kv.Key} {kv.Value}"));
    }

    private static GeneralSettings ReadGeneral(CharacterProfile profile)
    {
        if (profile.Settings is null) return new GeneralSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json))
            return new GeneralSettings();
        try
        {
            return JsonSerializer.Deserialize<GeneralSettings>(json.GetRawText())
                   ?? new GeneralSettings();
        }
        catch
        {
            return new GeneralSettings();
        }
    }

    private void WriteGeneral(CharacterProfile profile, GeneralSettings general)
    {
        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(general);
        _profile.Save();
    }
}
