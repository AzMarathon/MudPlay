using System.Collections.Specialized;
using System.ComponentModel;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// Squares the party roster with the game after a member dies in our room (user,
// 2026-10-17: "if a party member dies, we should check par, if they're no longer on
// the list, or it says invited, then remove them from the party").
//
// The death line. "<Name> is dead." is what everyone else in the room sees when a
// player dies (GAME_MECHANICS "Death lines & the miracle-save"). It names any
// player, party or not, so nothing here acts on a name that wasn't an ACTIVE member
// of ours when it died: a stranger isn't on the roster, a still-pending recruit
// was invited from the start, and the victim never sees its own line.
//
// The check. One `par` is asked for (PartyPoller.RequestParUnlessPending: none when
// a `par` already sent is unanswered or the timer's next is due within its window),
// and the first `par` reply read afterwards answers it:
//   - the member isn't listed (Stock takes a dead member off every list): the
//     roster has already dropped them, as PartyManager does for any member a `par`
//     leaves out;
//   - the member is listed [Invited] (Paradigm keeps the dead member's slot,
//     GAME_MECHANICS "Member death shows as an invited slot in `par`"): when we
//     lead, they're uninvited once the room is clear of combat, so the fight is
//     finished before party state is touched and the auto-party engine doesn't
//     hold a loop for a corpse it takes for an invitee.
// Only a death line seen for that member starts this (user, 2026-10-17): a
// party-splitting teleport also leaves members [Invited], and that is never a
// reason to uninvite anyone, so nothing here acts on the [Invited] state alone.
// Only the leader can uninvite, so a follower's part ends with the roster. A
// member invited again after the death (by the user or the auto-party engine), or
// seen following again, is no longer the corpse's slot, and isn't uninvited. The
// uninvite this sends is its own: UninvitedForDeath tells the auto-party engine,
// which otherwise reads an uninvite as the user turning that player away.
//
// A member who was our leader is left to the game's own "You are no longer
// following <Name>." line, which ends our follow.
//
// Read-only on party state: it reads PartyState and calls the public
// PartyManager.Uninvite (which routes the removal through the server echo), so
// PartyManager stays the sole writer of every roster field.
public sealed class PartyDeathRosterCleanup : IDisposable
{
    // Surfaced in the log timeline for each decision.
    public const string LogCategory = "Party";

    private readonly PartyState _party;
    private readonly PartyManager _manager;
    private readonly MovementCoordinator _coordinator;
    private readonly PartyPoller _poller;
    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();

    // given-name → when the death was seen, for the deaths a `par` reply hasn't
    // answered yet.
    private readonly Dictionary<string, DateTimeOffset> _awaitingPar =
        new(StringComparer.OrdinalIgnoreCase);

    // given-name → when the death was seen, for the dead members we lead that are
    // to be uninvited once they show [Invited] and the room is clear. Bounded by
    // CleanupWindow so a death that never surfaces as an [Invited] slot is dropped
    // rather than lingering forever.
    private readonly Dictionary<string, DateTimeOffset> _pendingDeaths =
        new(StringComparer.OrdinalIgnoreCase);

    // Give up on a recorded death if no `par` has settled it within this window.
    // Generous — a par poll is ~5 s and combat can run long.
    public TimeSpan CleanupWindow { get; set; } = TimeSpan.FromMinutes(2);

    // Overrides the wall clock for record/expiry timing.
    public Func<DateTimeOffset> NowProvider { get; set; } = () => DateTimeOffset.Now;

    // given-name → when this uninvited them, for UninvitedForDeath.
    private readonly Dictionary<string, DateTimeOffset> _ownUninvites =
        new(StringComparer.OrdinalIgnoreCase);

    // How long after its uninvite the game's "removed from your followers" echo
    // is taken for that uninvite.
    public static readonly TimeSpan OwnUninviteEchoWindow = TimeSpan.FromSeconds(30);

    // The last member death seen and what came of it, for the bug report.
    public string? LastDeathSummary { get; private set; }

    // The dead members waiting on a `par` reply or on their uninvite.
    public IReadOnlyCollection<string> AwaitingPar => _awaitingPar.Keys;
    public IReadOnlyCollection<string> AwaitingUninvite => _pendingDeaths.Keys;

    private bool _disposed;

    public PartyDeathRosterCleanup(
        MessageRouter router,
        PartyState party,
        PartyManager manager,
        MovementCoordinator coordinator,
        PartyPoller poller,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(poller);
        _party = party;
        _manager = manager;
        _coordinator = coordinator;
        _poller = poller;
        _log = log;

        _subs.Add(router.Subscribe(KnownPatterns.PartyMemberDied, OnMemberDied));
        // Ahead of PartyManager, whose [Invited] row for the invite would otherwise
        // let the pending uninvite go out on that same line.
        _subs.Add(router.Subscribe(KnownPatterns.PartyYouInvited, OnInvited, tieBreak: 1));
        _subs.Add(router.Subscribe(KnownPatterns.PartyFollowsYou, OnFollowsYou));
        _manager.ParReplyRead += OnParReplyRead;
        _coordinator.GatesChanged += OnGatesChanged;
        _party.Members.CollectionChanged += OnMembersChanged;
        foreach (PartyMember m in _party.Members)
            m.PropertyChanged += OnMemberPropertyChanged;
    }

    private void OnMemberDied(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string given = GivenNameOf(result.Groups[0]);
        if (given.Length == 0) return;
        if (ActiveMember(given) is not { } member) return;

        DateTimeOffset now = NowProvider();
        if (!_party.SelfIsLeader && (member.IsLeader || IsOurLeader(given)))
        {
            Note(now, $"{given} (our leader) is dead: the game ends our follow, so no `par` is asked for.");
            return;
        }

        _awaitingPar[given] = now;
        if (_party.SelfIsLeader) _pendingDeaths[given] = now;
        string asked = _poller.RequestParUnlessPending($"{given} is dead") switch
        {
            ParRequestOutcome.Sent => "`par` sent",
            ParRequestOutcome.OnItsWay => "a `par` already sent and not yet answered",
            ParRequestOutcome.DueSoon =>
                $"the timer's next `par`, due within {PartyPoller.ParDueWindow.TotalSeconds:0} s",
            _ => "no `par` could be sent now (master switch, out of the game or in a menu); the next one read",
        };
        Note(now, $"{given} is dead: {asked} will show whether they are still on the party list.");
        TryCleanup();
    }

    // The first `par` reply read after a death settles it: PartyManager has already
    // squared the roster with the reply when this is raised.
    private void OnParReplyRead()
    {
        if (_awaitingPar.Count == 0) return;
        DateTimeOffset now = NowProvider();
        foreach ((string given, DateTimeOffset seen) in _awaitingPar.ToArray())
        {
            _awaitingPar.Remove(given);
            if (now - seen > CleanupWindow) continue;
            PartyMember? row = RowOf(given);
            if (row is null)
            {
                _pendingDeaths.Remove(given);
                Note(now, $"`par` no longer lists {given}, dead: dropped from the party roster.");
            }
            else if (row.IsInvited)
            {
                // With the room already clear the uninvite went out as the row was
                // read, and said so.
                if (_pendingDeaths.ContainsKey(given))
                    Note(now, $"`par` lists {given}, dead, as [Invited]: uninvited once the room is clear of combat.");
                else if (!_party.SelfIsLeader)
                    Note(now, $"`par` lists {given}, dead, as [Invited]: only the leader can uninvite, so the row stays.");
            }
            else
            {
                _pendingDeaths.Remove(given);
                Note(now, $"`par` still lists {given} in the party after their death: the roster is left as it is.");
            }
        }
        TryCleanup();
    }

    // "You have invited <Name> to follow you." — our invite, typed or the
    // auto-party engine's, of a member whose uninvite is still waiting on combat.
    // They are wanted back, so the uninvite would undo it.
    private void OnInvited(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string given = GivenNameOf(result.Groups[0]);
        if (!_pendingDeaths.Remove(given)) return;
        Note(NowProvider(), $"{given} was invited again after their death: their [Invited] slot is no longer uninvited.");
    }

    // "<Name> started to follow you." — a dead member back in the party, also while
    // the `par` that would show their slot is still held back.
    private void OnFollowsYou(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string given = GivenNameOf(result.Groups[0]);
        if (!_pendingDeaths.Remove(given)) return;
        Note(NowProvider(), $"{given} is following again after their death: not uninvited.");
    }

    // Whether the game's "<Name> has been removed from your followers." answers an
    // uninvite of ours, for a dead member's slot, rather than the user's.
    public bool UninvitedForDeath(string name)
    {
        string given = GivenNameOf(name);
        if (!_ownUninvites.TryGetValue(given, out DateTimeOffset at)) return false;
        // One echo per uninvite: a later one for the same name is the user's.
        _ownUninvites.Remove(given);
        return NowProvider() - at <= OwnUninviteEchoWindow;
    }

    private void OnGatesChanged() => TryCleanup();

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (object? item in e.OldItems)
                if (item is PartyMember m) m.PropertyChanged -= OnMemberPropertyChanged;
        if (e.NewItems is not null)
            foreach (object? item in e.NewItems)
                if (item is PartyMember m) m.PropertyChanged += OnMemberPropertyChanged;
        TryCleanup();
    }

    // The [Invited] flip lands while the `par` rows are read, ahead of the reply's
    // end; with the room already clear the uninvite goes out then. A row that goes
    // back to following is a member back in the party, not the corpse's slot.
    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PartyMember.IsInvited)) return;
        if (sender is PartyMember { IsInvited: false } row
            && _pendingDeaths.Remove(GivenNameOf(row.Name)))
            Note(NowProvider(), $"{GivenNameOf(row.Name)} is following again after their death: not uninvited.");
        TryCleanup();
    }

    // The master switch (true = off): off, nobody is uninvited. The death stays
    // recorded, so the uninvite goes out if the switch is back on within the
    // cleanup window.
    public Func<bool>? MasterSwitchOff { get; set; }

    private void TryCleanup()
    {
        DateTimeOffset now = NowProvider();
        // A death no `par` reply has answered in the window is given up on.
        foreach ((string given, DateTimeOffset seen) in _awaitingPar.ToArray())
            if (now - seen > CleanupWindow) _awaitingPar.Remove(given);

        if (_pendingDeaths.Count == 0) return;
        if (MasterSwitchOff?.Invoke() == true) return;
        // Hold until the room is clear of combat.
        if (_coordinator.AssertedGates.Contains(MovementCoordinator.CombatGate)) return;

        foreach (string given in _pendingDeaths.Keys.ToArray())
        {
            if (now - _pendingDeaths[given] > CleanupWindow)
            {
                _pendingDeaths.Remove(given);
                continue;
            }
            if (!_party.SelfIsLeader) continue;
            if (RowOf(given) is not { IsInvited: true }) continue;
            _ownUninvites[given] = now;
            _manager.Uninvite(given);
            _pendingDeaths.Remove(given);
            Note(now, $"Uninvited {given}: the dead member's [Invited] slot is cleared.");
        }
    }

    private void Note(DateTimeOffset at, string message)
    {
        LastDeathSummary = $"{at:HH:mm:ss} {message}";
        _log?.Info(LogCategory, message);
    }

    private PartyMember? ActiveMember(string given)
    {
        foreach (PartyMember m in _party.Members)
            if (!m.IsSelf && !m.IsInvited
                && GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }

    private PartyMember? RowOf(string given)
    {
        foreach (PartyMember m in _party.Members)
            if (!m.IsSelf && GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }

    private bool IsOurLeader(string given)
        => _party.LeaderName is { Length: > 0 } leader
           && GivenNameOf(leader).Equals(given, StringComparison.OrdinalIgnoreCase);

    // First whitespace-delimited token. "Raijin WuzHere" → "Raijin". MajorMUD
    // addresses players by given name only and chat/par disagree on the form,
    // so roster matching is always by given name (mirrors PartyManager/PartyPoller).
    private static string GivenNameOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        string trimmed = name.Trim();
        int space = trimmed.IndexOf(' ');
        return space >= 0 ? trimmed[..space] : trimmed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
        _manager.ParReplyRead -= OnParReplyRead;
        _coordinator.GatesChanged -= OnGatesChanged;
        _party.Members.CollectionChanged -= OnMembersChanged;
        foreach (PartyMember m in _party.Members)
            m.PropertyChanged -= OnMemberPropertyChanged;
    }
}
