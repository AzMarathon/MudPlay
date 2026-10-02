using System;
using System.Collections.Generic;
using MudPlay.Models.Profile;

namespace MudPlay.Models.GameData;

// One user-defined scheduled / lifecycle event. Per-character; persisted
// on CharacterProfile.Events and consumed at runtime by EventManager.
//
// Flat shape: exactly one trigger-side field (AtTime, EveryAmount /
// EveryUnit) and exactly one action-side field (WalkToTarget, LoopName,
// AutoLairSetupName, or CommandText) is populated depending on TriggerType
// + ActionType. Other fields stay null so JSON round-tripping doesn't carry
// stale params from a previous trigger / action shape.
//
// JSON-friendly mutable POCO (settable properties) so System.Text.Json
// serialises without a custom converter and so the edit dialog can two-way
// bind to fields.
public sealed class ScheduledEvent
{
    // Readable label for the list view + log lines. May be empty.
    public string Name { get; set; } = string.Empty;

    // Event row exists but won't fire. Set manually OR by the saved-target
    // reconciler when a referenced loop / auto-lair name is no longer in
    // its manager's collection.
    public bool Disabled { get; set; }

    // A sound to play when the event fires: a built-in tone ("tone:chime") or a
    // sound file's path. Null = silent. Whether it plays, and how loud, is the
    // "Event sounds" row on Settings → Sounds.
    public string? Sound { get; set; }

    // Which lifecycle / schedule trigger fires this event.
    public EventTriggerType TriggerType { get; set; }

    // Wall-clock local fire time as HH:mm (24-hour) when TriggerType is
    // AtTime; null otherwise.
    public string? AtTime { get; set; }

    // Cadence amount paired with EveryUnit when TriggerType is Every; null
    // otherwise.
    public int? EveryAmount { get; set; }

    // Cadence unit paired with EveryAmount. Null when TriggerType is not
    // Every.
    public EventTimeUnit? EveryUnit { get; set; }

    // The checks a State trigger waits on; all must hold. Null for other
    // trigger types.
    public List<EventCondition>? Conditions { get; set; }

    // Which action the event runs when fired.
    public EventActionType ActionType { get; set; }

    // Coord target for EventActionType.WalkTo. Stored as a coord (not a
    // name) so renames in the active game-data set don't invalidate the
    // reference. Null for other action types.
    public RoomRef? WalkToTarget { get; set; }

    // Saved loop name (case-insensitive lookup against LoopManager.Loops)
    // for EventActionType.Loop. Null for other action types.
    public string? LoopName { get; set; }

    // Saved auto-lair setup name (case-insensitive lookup against
    // LairManager.Setups) for EventActionType.AutoLair. Null for other
    // action types.
    public string? AutoLairSetupName { get; set; }

    // Free-form command text for EventActionType.Command. Supports
    // multi-fire via ^M and ; separators — each chunk is split out and sent
    // as its own CR-terminated line. Null for other action types.
    public string? CommandText { get; set; }

    // Sweep mode for EventActionType.Roomba. Null for other action types.
    public EventRoombaMode? RoombaMode { get; set; }

    // ----- Boss trigger ----------------------------------------------

    // The boss (BossDef.Name) and moment a Boss trigger watches, and how many
    // minutes before an early window / guaranteed spawn / cleanup it fires.
    public string? BossName { get; set; }
    public EventBossMoment? BossMoment { get; set; }
    public int? BossLeadMinutes { get; set; }
    // Which early spawn window (a Bosses tab column) an EarlyWindow moment watches,
    // as its fraction of the full timer — Paradigm 0.80 / 0.90 / 0.95 (-20% / -10% /
    // -5%), Stock 0.875. Null = the earliest.
    public double? BossWindowFraction { get; set; }

    // ----- Action parameters -----------------------------------------

    // How long EventActionType.Wait stands still.
    public int? WaitSeconds { get; set; }

    // ----- Stop after (Loop / AutoLair) ------------------------------

    // A loop or auto-lair never ends by itself; these end it so the Then step
    // runs. Whichever is reached first wins; none set = it runs until stopped
    // by hand, and Then never runs.
    public int? StopAfterLaps { get; set; }
    public int? StopAfterMinutes { get; set; }
    // Stop at a moment on this boss's timer (BossDef.Name) — the same choices as a
    // Boss trigger: a timer column hitting 0, the guaranteed spawn, the kill, a
    // cleanup reset, optionally minutes early.
    public string? StopBossName { get; set; }
    public EventBossMoment? StopBossMoment { get; set; }
    public double? StopBossWindowFraction { get; set; }
    public int? StopBossLeadMinutes { get; set; }
    // Stop once all of these hold.
    public List<EventCondition>? StopConditions { get; set; }

    // ----- Then (after the action is done) ---------------------------

    // Null on events saved before Then existed: a walk-to resumes what it
    // interrupted (the old behavior), anything else does nothing (ResolvedThen).
    public EventThenType? Then { get; set; }
    public string? ThenLoopName { get; set; }
    public string? ThenAutoLairSetupName { get; set; }
    public RoomRef? ThenWalkTo { get; set; }
    // Another event, by Name.
    public string? ThenEventName { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public EventThenType ResolvedThen =>
        Then ?? (ActionType == EventActionType.WalkTo ? EventThenType.Resume : EventThenType.Nothing);

    // Tries to parse AtTime as 24-hour HH:mm into a TimeOnly. Returns null
    // when the field is blank or malformed. Centralised here so the editor
    // + engine agree on the format.
    public TimeOnly? TryParseAtTime() =>
        !string.IsNullOrWhiteSpace(AtTime)
        && TimeOnly.TryParseExact(AtTime, "HH:mm", out TimeOnly parsed)
            ? parsed
            : null;

    // Materialise the EveryAmount + EveryUnit pair as a TimeSpan. Returns
    // null when either field is unset or amount <= 0. Clamps amount to a
    // minimum of 1 to avoid degenerate zero-tick timers.
    public TimeSpan? TryParseEvery()
    {
        if (EveryAmount is not { } amount || amount <= 0) return null;
        if (EveryUnit is not { } unit) return null;
        return unit switch
        {
            EventTimeUnit.Seconds => TimeSpan.FromSeconds(amount),
            EventTimeUnit.Minutes => TimeSpan.FromMinutes(amount),
            EventTimeUnit.Hours   => TimeSpan.FromHours(amount),
            _ => null,
        };
    }
}
