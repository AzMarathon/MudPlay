using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Spells;

// Keeps every tracked buff's timer true to the time the character spends out of
// the game. "buffs survive disconnects, relogs", and "we should have buffs that
// we're tracking persist between sessions.. BUT for each buff we're tracking, as
// soon as we've disconnected all timers need to be paused until we're back in the
// game, then they all resume" (user, 2026-10-10).
//
// Two things follow, and this is where both are driven from, for the cast pass's
// timers (CastingDirector) and the hazard buff's (AutoHazardCounterProvisioner)
// alike:
//
//  • Out of the game the timers on our own character stand still. Out is the link
//    dropping, or the game being left for the board's menu with the link up
//    (InGameCapture tells both); back in is the first in-game prompt. Each store
//    does its own freezing and gives the time back on the way in. A timer on a
//    party member is left to run (CastingDirector.ResumeBuffTimers says why).
//  • What they had left is written to the character's profile whenever it is
//    saved while the character is out of the game, and put back, still standing,
//    when that profile is next loaded. A save made in the game writes none: time
//    only stops while the character is out, so a record taken in the game would
//    be wrong by however long the client ran on after it. A client that dies in
//    the game leaves no record, and the buffs are raised afresh as they always
//    were after a restart.
//
// None of it is automation: it happens with the master switch off as much as on,
// and a restore sends nothing (the cast pass stays latched off until the game is
// entered, where its own switches decide).
public sealed class BuffTimerKeeper
{
    public const string LogCategory = "BuffTimers";

    private readonly CastingDirector _director;
    private readonly AutoHazardCounterProvisioner _hazard;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Func<string?> _profileName;
    private readonly Func<string?> _realmKey;
    private readonly Action _saveProfile;
    private readonly Func<DateTime> _utcNow;
    private readonly LogService? _log;

    // The character is in the game. False from the start: a client that has just
    // opened is not, until its first in-game prompt.
    private bool _inGame;

    // The client is closing with the character still in the game: the timers have
    // been stopped for the last save, which is to carry them.
    private bool _closing;

    // When the timers now standing still stopped, for the record and the reports.
    private DateTime? _frozenAtUtc;

    // The realm and the profile they stopped under. The record is written with these
    // and never with whatever is current at a later save: a realm reassigned or a
    // profile saved under another name while the character is out of the game is
    // another character, and its saves must not relabel these timers as its own.
    private (string? Realm, string? Profile)? _frozenFor;

    // What the last profile load put back, for the bug report.
    private string? _restoredNote;

    // What the last profile swap in the game threw away, for the bug report.
    private string? _tossedNote;

    // profile: the loaded profile when it is a named character's, else null (the
    // default profile is the template new characters are made from, and timers
    // written there would be handed to each). realmKey: the BBS and realm now
    // played, "bbs/realm". saveProfile must not throw: one caller runs inside the
    // disconnect handler.
    public BuffTimerKeeper(
        CastingDirector director,
        AutoHazardCounterProvisioner hazard,
        Func<CharacterProfile?> profile,
        Func<string?> profileName,
        Func<string?> realmKey,
        Action saveProfile,
        Func<DateTime>? utcNow = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(director);
        ArgumentNullException.ThrowIfNull(hazard);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profileName);
        ArgumentNullException.ThrowIfNull(realmKey);
        ArgumentNullException.ThrowIfNull(saveProfile);
        _director = director;
        _hazard = hazard;
        _profile = profile;
        _profileName = profileName;
        _realmKey = realmKey;
        _saveProfile = saveProfile;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log;
    }

    // The character left the game. Stops the timers on it, and on the way out of the
    // game (not when it was out already) saves what each had left.
    public void NoteLeftGame(string why)
    {
        _director.PauseBuffTimers();
        _hazard.Pause(why);
        if (!_inGame) return;
        _inGame = false;
        Freeze();
        string still = $"out of the game ({why}) — {CountText(StandingStill())} stand still until it is entered again";
        // The default profile takes no timers (WriteTo), so there is nothing to save
        // it for, and a save here would rewrite that file at every disconnect.
        if (_profile() is null)
        {
            _log?.Info(LogCategory, $"{still}; not saved, no character's profile is loaded");
            return;
        }
        _log?.Info(LogCategory, $"{still}; saved with the character");
        _saveProfile();
    }

    // An in-game prompt: asked on every one. On the first after time out of the game
    // every timer runs on with what it had left, and the record in the profile, now
    // spent, is cleared.
    public void NoteBackInGame()
    {
        _director.ResumeBuffTimers();
        _hazard.Resume();
        // A prompt means the client did not close after all, whatever it had begun.
        _closing = false;
        if (_inGame) return;
        _inGame = true;
        _frozenAtUtc = null;
        _frozenFor = null;
        if (_profile()?.BuffTimers is null) return;
        _log?.Info(LogCategory, "back in the game — the timers saved with the character run on, and the saved copy is cleared");
        _saveProfile();
    }

    // The client is closing. With the character still in the game the link goes
    // with the client, so the timers stop here and the closing save carries them.
    public void NoteClientClosing()
    {
        _closing = true;
        _director.PauseBuffTimers();
        _hazard.Pause("the client is closing");
        if (!_inGame) return;
        Freeze();
        _log?.Info(LogCategory, $"the client is closing — {CountText(StandingStill())} saved with the character");
    }

    private void Freeze()
    {
        _frozenAtUtc = _utcNow();
        _frozenFor = (_realmKey(), _profileName());
    }

    // ProfileService.ProfileClosed: the loaded profile is going, ahead of the load
    // of the next. With the character in the game that is a swap under a live
    // session: "if they swap profiles while still connected, toss the timers" (user,
    // 2026-10-10). The timers are then neither profile's. The outgoing one was
    // saved on its way out with none (WriteTo, in the game), and the incoming one's
    // own saved copy is dropped as it loads (RestoreFrom). The live ones are wiped
    // here and not left to the reset its load brings, so the rule and its log line
    // do not hang on what two other subscribers happen to do.
    //
    // Out of the game it is not that kind of swap: the timers stand still and were
    // saved with the outgoing profile, and the incoming one's are put back. The
    // restart for an update is of this kind, since it drops the link first.
    public void NoteProfileClosed()
    {
        if (!_inGame) return;
        IReadOnlyList<ActiveBuffTimer> live = _director.SnapshotActiveBuffs();
        int onMembers = live.Count(t => t.Target.Length != 0);
        int tossed = live.Count + _hazard.SnapshotForSave().Count;
        _director.ResetBuffTracking();
        _hazard.Forget("the profile was swapped with the character in the game");
        _tossedNote = $"{CountText(tossed)} tossed at {Local(_utcNow())}: the profile was swapped with the character in the game";
        _log?.Info(LogCategory,
            $"the profile was swapped with the character in the game — {CountText(tossed)} tossed"
            + (onMembers > 0 ? $" ({onMembers} of them on party members)" : "")
            + ": not saved with the outgoing profile, and none carried into the next");
    }

    // ProfileService.ProfileSaving: the timers go into the profile while the
    // character is out of the game, and come out of it while it is in.
    public void WriteTo(CharacterProfile saving)
    {
        ArgumentNullException.ThrowIfNull(saving);
        if (!ReferenceEquals(_profile(), saving)) return;
        if (_inGame && !_closing)
        {
            saving.BuffTimers = null;
            return;
        }

        // The realm or the profile's name changed while the character was out of the
        // game: what stands still here was another character's. It is dropped, where
        // writing it out would hand it to this one under its own name.
        (string? Realm, string? Profile) now = (_realmKey(), _profileName());
        if (_frozenFor is { } was && (!Same(was.Realm, now.Realm) || !Same(was.Profile, now.Profile)))
        {
            string why = $"they stood still for '{was.Profile}' on {was.Realm ?? "no realm"}, "
                + $"and the profile is now '{now.Profile}' on {now.Realm ?? "no realm"}";
            int dropped = _director.ClearCarriedSelfTimers(why) + _hazard.ForgetCarried(why);
            _frozenFor = now;
            _restoredNote = null;
            _log?.Info(LogCategory, $"dropped {CountText(dropped)}: {why}");
        }

        List<SavedBuffTimer> buffs = _director.SnapshotSelfTimersForSave();
        List<SavedHazardBuff> hazardBuffs = _hazard.SnapshotForSave();
        (string? Realm, string? Profile) key = _frozenFor ?? now;
        saving.BuffTimers = buffs.Count + hazardBuffs.Count == 0
            ? null
            : new SavedBuffTimers
            {
                FrozenAtUtc = _frozenAtUtc ?? _utcNow(),
                Realm = key.Realm,
                Profile = key.Profile,
                Buffs = buffs,
                HazardBuffs = hazardBuffs,
            };
    }

    // ProfileService.ProfileLoaded, after the cast pass and the hazard counter have
    // been reset for the new character and its game data is the active set: put
    // back what the profile was saved with. The timers stand still until the game
    // is entered.
    public void RestoreFrom(CharacterProfile loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        _restoredNote = null;
        _frozenAtUtc = null;
        _frozenFor = null;
        if (!ReferenceEquals(_profile(), loaded) || loaded.BuffTimers is not { } saved) return;

        int kept = saved.Buffs.Count + saved.HazardBuffs.Count;
        string? why =
            _inGame ? "the profile was loaded with a character already in the game"
            : !Same(saved.Realm, _realmKey()) ? $"they were saved on {saved.Realm ?? "no realm"}, and this is {_realmKey() ?? "no realm"}"
            : !Same(saved.Profile, _profileName()) ? $"they were saved for the profile '{saved.Profile}', not this one"
            : null;
        if (why is not null)
        {
            loaded.BuffTimers = null;
            _log?.Info(LogCategory, $"dropped {CountText(kept)} saved with the character: {why}");
            return;
        }

        int restored = _director.RestoreSelfTimers(saved.Buffs) + _hazard.Restore(saved.HazardBuffs);
        _frozenAtUtc = saved.FrozenAtUtc;
        _frozenFor = (saved.Realm, saved.Profile);
        string from = $"the session that left the game at {Local(saved.FrozenAtUtc)}";
        _restoredNote = $"{CountText(restored)} put back from {from}";
        _log?.Info(LogCategory,
            $"restored {CountText(restored)} from {from} ({Ago(_utcNow() - saved.FrozenAtUtc)} ago) — standing still until the game is entered"
            + (restored < kept ? $"; dropped {kept - restored} this game data or its settings no longer know" : ""));
    }

    // For the bug report: running or standing still, since when, what a load put
    // back, and what a swap in the game threw away.
    public IReadOnlyList<string> Describe()
    {
        List<string> lines = new()
        {
            _inGame && !_closing
                ? "running: the character is in the game"
                : _frozenAtUtc is { } at
                    ? $"standing still since {Local(at)}: the character is out of the game"
                    : "standing still: the character has not been in the game yet",
        };
        if (_restoredNote is not null) lines.Add(_restoredNote);
        if (_tossedNote is not null) lines.Add(_tossedNote);
        lines.Add(_profile()?.BuffTimers is { } saved
            ? $"saved with the character: {saved.Buffs.Count} buff timer(s), {saved.HazardBuffs.Count} hazard buff(s)"
            : "saved with the character: none");
        return lines;
    }

    private int StandingStill() => _director.SnapshotSelfTimersForSave().Count + _hazard.SnapshotForSave().Count;

    private static string CountText(int timers) => timers == 1 ? "1 buff timer" : $"{timers} buff timers";

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string Ago(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min"
        : $"{Math.Max(0, (int)span.TotalSeconds)} s";
}
