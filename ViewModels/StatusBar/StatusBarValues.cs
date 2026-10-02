using System.Globalization;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.StatusBar;

// The current text of each status-bar item. Every reader is a few property reads
// and a format — nothing here touches game data or the wire — because the bar asks
// for the items it shows twice a second.
public static class StatusBarValues
{
    // Items whose value is one of the main window's own status properties: the bar
    // refreshes them the moment that property changes instead of on its timer, so
    // the tick countdowns keep their own cadence.
    public static string? SourceProperty(string id) => id switch
    {
        "engine" => nameof(MainWindowViewModel.EngineActionBadge),
        "location" => nameof(MainWindowViewModel.LocationText),
        "target" => nameof(MainWindowViewModel.TargetHpText),
        "statline" => nameof(MainWindowViewModel.IsStatlineMismatchVisible),
        "tick" => nameof(MainWindowViewModel.CombatTickText),
        "hptick" => nameof(MainWindowViewModel.HpTickText),
        "matick" => nameof(MainWindowViewModel.MaTickText),
        "connection" => nameof(MainWindowViewModel.ConnectionStatusText),
        _ => null,
    };

    public static string Read(string id, MainWindowViewModel main)
    {
        AppServices svc = AppServices.Current;
        PlayerState state = svc.PlayerState;
        PlayerStats stats = svc.PlayerStats;
        return id switch
        {
            "engine" => main.EngineActionBadge,
            "location" => main.LocationText,
            "target" => main.TargetHpText,
            "statline" => main.IsStatlineMismatchVisible ? "STATLINE MISMATCH" : string.Empty,
            "tick" => main.CombatTickText,
            "hptick" => main.HpTickText,
            "matick" => main.MaTickText,
            "connection" => main.IsReconnectCountdownVisible
                ? $"{main.ConnectionStatusText} · {main.ReconnectCountdownText}"
                : main.ConnectionStatusText,

            "profile" => svc.Profile.CurrentProfileName ?? string.Empty,
            "name" => stats.Name,
            "level" => stats.Level > 0 ? $"Lvl {stats.Level}" : string.Empty,
            "class" => $"{stats.Race} {stats.Class}".Trim(),
            "lives" => stats.Level > 0 ? $"Lives {stats.Lives}" : string.Empty,
            "bbs" => main.ActiveBbsName ?? string.Empty,
            "gamedata" => svc.GameData.ActiveSet ?? string.Empty,
            "combatprofile" => CombatProfile(svc),

            "hp" => state.HasPromptData ? $"HP {state.Hp}/{state.MaxHp}" : string.Empty,
            "hppct" => state.HasPromptData && state.MaxHp > 0 ? $"HP {Percent(state.Hp, state.MaxHp)}%" : string.Empty,
            "mana" => state.HasPromptData && state.MaxMa > 0 ? $"MA {state.Ma}/{state.MaxMa}" : string.Empty,
            "manapct" => state.HasPromptData && state.MaxMa > 0 ? $"MA {Percent(state.Ma, state.MaxMa)}%" : string.Empty,
            "position" => state.Position == PlayerPosition.Standing ? string.Empty : state.Position.ToString(),
            "stealth" => state.IsHidden ? "Hidden" : state.IsSneaking ? "Sneaking" : string.Empty,
            "encumbrance" => state.Encumbrance == EncumbranceLevel.Unknown ? string.Empty : state.Encumbrance.ToString(),

            "roomkey" => svc.RoomTracker.State.CurrentRoom?.Key.ToString() ?? string.Empty,
            "room" => svc.RoomTracker.State.CurrentRoom?.Name ?? string.Empty,
            "loop" => RunningLoop(svc)?.Name ?? string.Empty,
            "lap" => RunningLoop(svc) is null ? string.Empty : $"Lap {svc.LoopRunner.CompletedLaps + 1}",
            "destination" => svc.Walker.State != WalkState.Idle && svc.Walker.Destination is { } to ? $"To {to}" : string.Empty,

            "fighting" => state.InCombat ? svc.Combat.CurrentTarget ?? string.Empty : string.Empty,
            "exprate" => $"{RateText.Compact(svc.SessionActivity.Snapshot().ExperiencePerHour)}/hr",
            "exptnl" => stats.Level > 0 ? $"TNL {RateText.Compact(Math.Max(0, stats.ExpToNext))}" : string.Empty,
            "sessionexp" => $"Exp {RateText.Compact(svc.SessionActivity.Snapshot().ExperienceEarned)}",
            "kills" => $"Kills {svc.SessionActivity.Snapshot().MonstersKilled}",
            "sessiontime" => Duration(svc.SessionActivity.Snapshot().TimeOnline),
            "party" => Party(svc),

            "auto" => AutoEngines(main),
            "cash" => Cash(svc.Inventory.Snapshot.Currency),
            "clock" => DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    private static int Percent(int value, int max) => (int)Math.Round(100.0 * value / max);

    private static Loop? RunningLoop(AppServices svc) =>
        svc.LoopRunner.State == LoopState.Idle ? null : svc.LoopRunner.CurrentLoop;

    private static string CombatProfile(AppServices svc)
    {
        int active = svc.CombatProfiles.ActiveIndex;
        var profiles = svc.CombatProfiles.Profiles;
        if (active < 0 || active >= profiles.Count) return string.Empty;
        return $"P{active + 1} {profiles[active].Name}".TrimEnd();
    }

    private static string Party(AppServices svc)
    {
        PartyState party = svc.PartyState;
        if (!party.IsInParty) return string.Empty;
        string size = $"Party {party.Members.Count}";
        return party.SelfIsLeader ? $"{size} · leading"
            : string.IsNullOrEmpty(party.LeaderName) ? size
            : $"{size} · {party.LeaderName} leads";
    }

    private static string Duration(TimeSpan time) =>
        time <= TimeSpan.Zero ? string.Empty
        : $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";

    private static string AutoEngines(MainWindowViewModel main)
    {
        List<string> on = new(10);
        if (main.IsAutoCombatActive) on.Add("Combat");
        if (main.IsAutoNukeActive) on.Add("Nuke");
        if (main.IsAutoHealActive) on.Add("Heal");
        if (main.IsAutoRestActive) on.Add("Rest");
        if (main.IsAutoBlessActive) on.Add("Bless");
        if (main.IsAutoLightActive) on.Add("Light");
        if (main.IsAutoGetItemsActive) on.Add("Items");
        if (main.IsAutoGetCashActive) on.Add("Cash");
        if (main.IsAutoSneakActive) on.Add("Sneak");
        if (main.IsAutoHideActive) on.Add("Hide");
        if (main.IsAutoSearchActive) on.Add("Search");
        return on.Count == 0 ? "Auto: off" : "Auto: " + string.Join(' ', on);
    }

    // "Cash 2r 3p 40g" — every coin held, highest first.
    private static string Cash(CurrencyHoldings held)
    {
        if (held.TotalCoinCount == 0) return string.Empty;
        List<string> coins = new(5);
        if (held.Runic > 0) coins.Add($"{held.Runic}r");
        if (held.Platinum > 0) coins.Add($"{held.Platinum}p");
        if (held.Gold > 0) coins.Add($"{held.Gold}g");
        if (held.Silver > 0) coins.Add($"{held.Silver}s");
        if (held.Copper > 0) coins.Add($"{held.Copper}c");
        return "Cash " + string.Join(' ', coins);
    }
}
