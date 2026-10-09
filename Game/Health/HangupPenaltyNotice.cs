using MudPlay.Models.Settings;

namespace MudPlay.Game.Health;

// Words a realm's hang-up penalty (RealmProfile, Settings → BBS) for the two
// places that show it: the program log when a hang-up goes out, and the bug
// report. The penalty is the board's own (GAME_MECHANICS
// "Hang-up / lost carrier"); the client reports it, and after one looks for the
// items it dropped (MaxItemsDropped). When it hangs up, and whether, is decided by
// Disable Hangups, the health settings, the PvP actions and @panic, and nothing
// here feeds back into any of them.
public static class HangupPenaltyNotice
{
    public const string LogCategory = "Hangup";

    // The board's own limits on the three numbers.
    public const int MaxPercent = 100;
    public const int MaxItems = 100;

    // The HP share as a board can set it: each end 0–100, and the upper end never
    // under the lower.
    public static (int From, int To) HpRange(int from, int to)
    {
        from = Math.Clamp(from, 0, MaxPercent);
        return (from, Math.Max(from, Math.Clamp(to, 0, MaxPercent)));
    }

    public static int Items(int items) => Math.Clamp(items, 0, MaxItems);

    // The most items the realm's settings say one hang-up can cost: the PvP side's
    // count, or the monster side's when that side is ticked and takes more, since
    // the client can't know afterwards which side applied. 0 when no items are
    // dropped. Read on the way back into the game by
    // Game.Inventory.HangupItemRecheck, which looks for what was dropped and takes
    // back no more than this; it has no say in a hang-up either.
    public static int MaxItemsDropped(RealmProfile? realm)
    {
        if (realm is not { HangupPenaltyEnabled: true }) return 0;
        int pvp = Items(realm.HangupPvpItemsDropped);
        return realm.HangupPvePenaltyEnabled ? Math.Max(pvp, Items(realm.HangupPveItemsDropped)) : pvp;
    }

    // The realm's penalties in one phrase, for the bug report.
    public static string Describe(RealmProfile? realm)
    {
        if (realm is not { HangupPenaltyEnabled: true }) return "none";
        return $"PvP: {Pvp(realm)}; PvE: {(realm.HangupPvePenaltyEnabled ? Pve(realm) : "not penalised")}";
    }

    // The log line for a hang-up going out now, or null when the realm's settings
    // don't penalise it.
    //   pvp      — the hang-up is the PvP response's, or a fight with a player is
    //              under way; it outranks inCombat, which a fight with a player
    //              raises too.
    //   inCombat — PlayerState.InCombat.
    // With neither, the client can't tell whether something is attacking us, so
    // the line gives the rule without saying which side this hang-up falls under.
    public static string? ForHangup(RealmProfile? realm, bool pvp, bool inCombat)
    {
        if (realm is not { HangupPenaltyEnabled: true }) return null;
        if (pvp) return $"This realm penalises a hang-up in PvP: {Pvp(realm)}.";
        if (inCombat)
            return realm.HangupPvePenaltyEnabled
                ? $"This realm penalises a hang-up in combat with monsters: {Pve(realm)}."
                : null;
        return realm.HangupPvePenaltyEnabled
            ? $"This realm penalises a hang-up in PvP ({Pvp(realm)}) and in combat with monsters ({Pve(realm)})."
            : $"This realm penalises a hang-up in PvP only: {Pvp(realm)}.";
    }

    private static string Pvp(RealmProfile realm) =>
        Side(realm.HangupPvpHpFromPercent, realm.HangupPvpHpToPercent, realm.HangupPvpItemsDropped);

    private static string Pve(RealmProfile realm) =>
        Side(realm.HangupPveHpFromPercent, realm.HangupPveHpToPercent, realm.HangupPveItemsDropped);

    // Read through the limits, so a hand-edited file still gives a sane line.
    private static string Side(int hpFrom, int hpTo, int itemCount)
    {
        (int from, int to) = HpRange(hpFrom, hpTo);
        int items = Items(itemCount);
        string? hp = to == 0 ? null : from == to ? $"{to}% of max HP" : $"{from}–{to}% of max HP";
        string? dropped = items == 0 ? null : items == 1 ? "up to 1 item" : $"up to {items} items";
        return (hp, dropped) switch
        {
            ({ } h, { } d) => $"{h} and {d}",
            ({ } h, null) => h,
            (null, { } d) => d,
            _ => "no HP or items set",
        };
    }
}
