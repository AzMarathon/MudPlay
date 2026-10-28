using System.Collections.Specialized;
using System.ComponentModel;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// Tells a follower's HealthManager when the party has stopped to rest, so it can
// top off in the downtime: the leader resting or meditating, or another member
// doing so with the leader not having moved us since it was seen.
//
// A member's Resting / Meditating flags are only as fresh as the last `par` poll
// (or `<name> stops to rest.`), so a member seen resting before the leader dragged
// us on may well be standing by now. A follow drag therefore forgets every member
// rest seen so far, and only a rest seen after it (a flag that comes up, or the
// next `par` still listing it) counts again. The leader's own rest needs no such
// check: a leader who is resting isn't walking.
//
// Edge-triggered: the callback fires only when the answer changes, so a member
// sitting down while we already rest for the leader doesn't poke the engine.
//
// Read-only on party state — subscribes to PartyState.Members and each member's
// property changes, never writing a PartyMember field, so PartyManager stays the
// single writer.
public sealed class PartyRestWatcher : IDisposable
{
    private const string LogCategory = "PartyRest";

    private readonly PartyState _party;
    private readonly PartyManager _manager;
    private readonly Action _onPartyRestChanged;
    private readonly LogService? _log;
    private readonly IDisposable _dragSub;
    private readonly HashSet<PartyMember> _watched = new();
    private readonly HashSet<PartyMember> _restSeenSinceDrag = new();
    private string? _restingFor;

    public PartyRestWatcher(PartyState party, PartyManager manager, MessageRouter router,
        Action onPartyRestChanged, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(onPartyRestChanged);
        _party = party;
        _manager = manager;
        _onPartyRestChanged = onPartyRestChanged;
        _log = log;

        _party.Members.CollectionChanged += OnMembersChanged;
        foreach (PartyMember m in _party.Members) Watch(m);
        _manager.ParReplyRead += OnParReplyRead;
        _dragSub = router.Subscribe(KnownPatterns.PartyFollowMove, _ => OnFollowDrag());
        _restingFor = RestingFor();
    }

    // Who the party is resting for, as the program log words it ("the leader is
    // resting", "Raijin WuzHere is meditating"); null when we lead, are solo, or
    // nobody counts. Live read — recomputed on each access.
    public string? PartyRestingFor => RestingFor();

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (PartyMember m in e.OldItems) Unwatch(m);
        if (e.NewItems is not null)
            foreach (PartyMember m in e.NewItems) Watch(m);
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (PartyMember m in _watched) m.PropertyChanged -= OnMemberPropertyChanged;
            _watched.Clear();
            _restSeenSinceDrag.Clear();
            foreach (PartyMember m in _party.Members) Watch(m);
        }
        Reevaluate();
    }

    private void Watch(PartyMember m)
    {
        if (!_watched.Add(m)) return;
        m.PropertyChanged += OnMemberPropertyChanged;
        NoteRestSeen(m);
    }

    private void Unwatch(PartyMember m)
    {
        if (_watched.Remove(m)) m.PropertyChanged -= OnMemberPropertyChanged;
        _restSeenSinceDrag.Remove(m);
    }

    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PartyMember.Resting)
                                or nameof(PartyMember.Meditating)
                                or nameof(PartyMember.IsLeader)
                                or nameof(PartyMember.IsSelf))) return;
        if (sender is PartyMember m) NoteRestSeen(m);
        Reevaluate();
    }

    // A `par` read after a drag re-confirms the members it still lists resting,
    // whose flags didn't change and so raised nothing.
    private void OnParReplyRead()
    {
        foreach (PartyMember m in _party.Members) NoteRestSeen(m);
        Reevaluate();
    }

    private void OnFollowDrag()
    {
        _restSeenSinceDrag.Clear();
        Reevaluate();
    }

    private void NoteRestSeen(PartyMember m)
    {
        if (m.Resting || m.Meditating) _restSeenSinceDrag.Add(m);
        else _restSeenSinceDrag.Remove(m);
    }

    private void Reevaluate()
    {
        string? now = RestingFor();
        if ((now is null) == (_restingFor is null)) { _restingFor = now; return; }
        _restingFor = now;
        _log?.Info(LogCategory, now is null
            ? "The party is no longer resting."
            : $"The party has stopped to rest: {now}.");
        _onPartyRestChanged();
    }

    private string? RestingFor()
    {
        if (!_party.IsInParty || _party.SelfIsLeader) return null;
        foreach (PartyMember m in _party.Members)
            if (m.IsLeader && (m.Resting || m.Meditating))
                return m.Meditating ? "the leader is meditating" : "the leader is resting";
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf || m.IsLeader || !_restSeenSinceDrag.Contains(m)) continue;
            if (m.Resting || m.Meditating)
                return $"{m.Name} is {(m.Meditating ? "meditating" : "resting")}";
        }
        return null;
    }

    public void Dispose()
    {
        _party.Members.CollectionChanged -= OnMembersChanged;
        _manager.ParReplyRead -= OnParReplyRead;
        _dragSub.Dispose();
        foreach (PartyMember m in _watched) m.PropertyChanged -= OnMemberPropertyChanged;
        _watched.Clear();
        _restSeenSinceDrag.Clear();
    }
}
