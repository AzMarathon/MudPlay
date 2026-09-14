using System;

namespace MudPlay.Services.Update;

// When the automatic re-check is allowed to run. Twice a day at fixed local-clock
// slots rather than "every N hours since launch": a client left running for a week
// then checks at predictable times instead of drifting, and two clients started
// hours apart don't hammer the API on their own staggered schedules.
//
// Pure date math so the cadence is testable without waiting out a timer.
public static class UpdateSchedule
{
    // Morning and evening, local time. Deliberately off the hour boundaries that
    // everything else fires on, and far from the start of a session.
    private static readonly TimeSpan[] Slots =
    {
        new(9, 0, 0),
        new(21, 0, 0),
    };

    // The next slot strictly after `local`. Rolls to tomorrow's first slot once the
    // day's last one has passed. Strictly-after matters: called again right after a
    // check fires, it must return the NEXT slot, not the one that just went off.
    public static DateTime NextSlotAfter(DateTime local)
    {
        foreach (TimeSpan slot in Slots)
        {
            DateTime today = local.Date + slot;
            if (today > local) return today;
        }
        return local.Date.AddDays(1) + Slots[0];
    }
}
