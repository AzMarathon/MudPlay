using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Sounds;

// One thing the client does that can play a sound. Every cue starts off. An empty
// DefaultSound marks a cue with no sound of its own: each trigger or event names
// the sound it plays, and the cue only says whether they play and how loud.
// DefaultEvery is non-zero only for the counted cues (a sound every N loops / kills).
public sealed record SoundCue(
    string Id, string Group, string Label, string Description,
    string DefaultSound, int DefaultEvery = 0)
{
    public bool HasOwnSound => DefaultSound.Length > 0;
}

// The catalogue of sound cues, in the order the Sounds tab lists them. Ids are
// persisted (SoundSettings.Cues keys) — never rename one.
public static class SoundCues
{
    public const string LevelUp = "level-up";
    public const string BossKilled = "boss-killed";
    public const string BossWindow = "boss-window";
    public const string BossReady = "boss-ready";
    public const string EventFired = "event-fired";
    public const string AutoTrain = "auto-train";
    public const string AutoSell = "auto-sell";
    public const string LoopMilestone = "loop-milestone";
    public const string KillMilestone = "kill-milestone";
    public const string WalkFinished = "walk-finished";
    public const string Telepath = "telepath";
    public const string PartyInvite = "party-invite";
    public const string PartyMemberDown = "party-member-down";
    public const string Death = "death";
    public const string MortallyWounded = "mortally-wounded";
    public const string Flee = "flee";
    public const string NavigationStopped = "navigation-stopped";
    public const string Disconnected = "disconnected";
    public const string Reconnected = "reconnected";
    public const string Trigger = "trigger";

    private const string Progress = "Progress", Automation = "Automation", Bosses = "Bosses",
        ChatParty = "Chat and party", Danger = "Danger", Connection = "Connection and triggers";

    public static IReadOnlyList<SoundCue> All { get; } = new SoundCue[]
    {
        new(LevelUp, Progress, "Level up", "You train a level.", SoundTones.Ding),
        new(LoopMilestone, Progress, "Loop milestone", "Every so many laps of the running loop, counted from when the loop was started.", SoundTones.Chime, 100),
        new(KillMilestone, Progress, "Kill milestone", "Every so many kills since this character was loaded.", SoundTones.Chime, 300),
        new(WalkFinished, Progress, "Walk finished", "A walk-to reaches its destination (not a loop lap or a detour).", SoundTones.Chime),

        new(AutoTrain, Automation, "Auto-training", "An auto-train trip sets off.", SoundTones.Chime),
        new(AutoSell, Automation, "Auto-selling", "An auto-sell trip sets off.", SoundTones.Coin),
        new(EventFired, Automation, "Event sounds", "An Event with a sound fires. Each event plays the sound picked in its editor; this sets whether they play and how loud.", string.Empty),

        new(BossKilled, Bosses, "Boss killed", "A boss on the Bosses table dies and its timer starts.", SoundTones.Fanfare),
        new(BossWindow, Bosses, "Boss spawn window opens", "A boss timer reaches its first early spawn window.", SoundTones.Chime),
        new(BossReady, Bosses, "Boss timer done", "A boss timer reaches its guaranteed respawn.", SoundTones.Alert),

        new(Telepath, ChatParty, "Telepath received", "Someone telepaths you (not an @-command).", SoundTones.Chime),
        new(PartyInvite, ChatParty, "Party invite", "Someone invites you to follow them.", SoundTones.Chime),
        new(PartyMemberDown, ChatParty, "Party member down", "A party member drops to the ground.", SoundTones.Alert),

        new(Death, Danger, "You died", "You are killed.", SoundTones.Low),
        new(MortallyWounded, Danger, "Mortally wounded", "You drop to the ground.", SoundTones.Alarm),
        new(Flee, Danger, "Fleeing", "A low-HP flee starts.", SoundTones.Alert),
        new(NavigationStopped, Danger, "Navigation stopped", "A walk or loop fails, or the client loses track of the room.", SoundTones.Alert),

        new(Disconnected, Connection, "Disconnected", "The connection drops.", SoundTones.Low),
        new(Reconnected, Connection, "Reconnected", "The connection comes back.", SoundTones.Chime),
        new(Trigger, Connection, "Trigger sounds", "A Trigger with a sound file fires. Each trigger plays its own file; this sets whether they play and how loud.", string.Empty),
    };

    public static SoundCue? Find(string id) => All.FirstOrDefault(c => c.Id == id);
}
