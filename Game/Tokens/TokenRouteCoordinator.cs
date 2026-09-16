using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Tokens;

// Executes a picked token route.
//
// SOLO: use the transport token, then resume the walk from its landing to the
// destination. If the current room has monsters/NPCs (a token can't be used there)
// it walks the overland route toward the destination and uses the token at the
// first monster-free room instead (auto-combat clears hostiles on the way; friendly-
// NPC rooms are walked past) — a genuinely-shorter token route always reaches a clear
// room before arrival.
//
// PARTY LEADER: same use flow, but after landing it regroups the party before walking
// on — telepaths each member `@do use <token>`, confirms each arrives (the "A gryphon
// drops <name> off in <location>!" line in the leader's landing room), and retries up
// to MaxRegroupRetries at RegroupIntervalMs. Fully regrouped → resume the walk; else
// fail out (RegroupFailed → nav header) and sit for user action. A party FOLLOWER
// declines (TryBegin returns false) so the caller walks the overland route instead.
//
// Delegate-injected (no live line stream) so it unit-tests without the app; driven by
// TokenTracker.TokenUsed / TokenTracker.MemberArrived, Walker.Event, and
// RoomTracker.StateChanged, all already on the UI thread — so it needs no locking.
public sealed class TokenRouteCoordinator
{
    private enum Phase
    {
        Idle,
        WalkingToClear,  // NPCs present at begin — walking overland, waiting for a clear room to use in.
        AwaitingUse,     // sent `use token of <place>`, waiting for the success line (TokenUsed).
        AwaitingLanding, // teleport succeeded, waiting for the room tracker to confirm the landing room.
        Regrouping,      // (leader) landed; telepathing members `@do use` and waiting for their arrivals.
    }

    private const int LandingConfirmTimeoutMs = 8000;
    private const int UseConfirmTimeoutMs = 6000;
    private const int PostLandSettleMs = 700;
    // Party regroup: re-send the `@do use` and re-check arrivals this often, up to this
    // many retries after the first wave, before failing out.
    private const int RegroupIntervalMs = 6000;
    private const int MaxRegroupRetries = 3;

    private readonly Func<bool> _inParty;
    private readonly Func<bool> _isLeader;
    private readonly Func<IReadOnlyList<string>> _membersToRegroup;
    private readonly Func<bool> _roomHasNpc;
    private readonly Action<RoomKey> _walkToDest;
    private readonly Action _stopWalker;
    private readonly Action<string> _send;
    private readonly Action<int, Action> _schedule;
    private readonly LogService? _log;

    // Fired when a party regroup fails after the retries — carries the nav-header reason.
    public event Action<string>? RegroupFailed;

    private Phase _phase = Phase.Idle;
    private string _place = "";          // normalized, for matching the success line
    private string _displayPlace = "";   // original casing/article, for building the use command
    private RoomKey _landing;
    private RoomKey _destination;
    private RoomKey? _lastRoom;
    // Invalidates a scheduled callback when the phase moves on before it fires (a
    // DispatcherTimer callback still runs after we've advanced).
    private int _generation;

    private readonly HashSet<string> _regroupPending = new(StringComparer.OrdinalIgnoreCase);
    private int _regroupRetries;

    public TokenRouteCoordinator(
        Func<bool> inParty,
        Func<bool> isLeader,
        Func<IReadOnlyList<string>> membersToRegroup,
        Func<bool> roomHasNpc,
        Action<RoomKey> walkToDest,
        Action stopWalker,
        Action<string> send,
        Action<int, Action> schedule,
        LogService? log = null)
    {
        _inParty = inParty ?? throw new ArgumentNullException(nameof(inParty));
        _isLeader = isLeader ?? throw new ArgumentNullException(nameof(isLeader));
        _membersToRegroup = membersToRegroup ?? throw new ArgumentNullException(nameof(membersToRegroup));
        _roomHasNpc = roomHasNpc ?? throw new ArgumentNullException(nameof(roomHasNpc));
        _walkToDest = walkToDest ?? throw new ArgumentNullException(nameof(walkToDest));
        _stopWalker = stopWalker ?? throw new ArgumentNullException(nameof(stopWalker));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _log = log;
    }

    public bool Active => _phase != Phase.Idle;

    // Begin executing a picked token route. Returns false — so the caller walks the
    // plain overland route instead — when in a party but NOT the leader (a follower
    // can't drive a token route). Solo or leader: use the token now if the room is
    // clear, else walk overland and use it at the first clear room; returns true.
    public bool TryBegin(string place, RoomKey landing, RoomKey destination)
    {
        if (string.IsNullOrWhiteSpace(place)) return false;
        if (_inParty() && !_isLeader())
        {
            _log?.Info("Tokens", $"token route to {place} declined — a party follower can't drive a token route; walking overland");
            return false;
        }

        _place = TokenCatalog.NormalizePlace(place);
        _displayPlace = place.Trim();
        _landing = landing;
        _destination = destination;
        _lastRoom = null;
        _regroupPending.Clear();
        _regroupRetries = 0;
        NextGeneration();

        if (!_roomHasNpc())
        {
            UseNow();
        }
        else
        {
            _phase = Phase.WalkingToClear;
            _log?.Info("Tokens", $"token route to {_place}: room not clear — walking overland until a monster-free room, then using the token");
            _walkToDest(_destination);
        }
        return true;
    }

    // Send the token use and wait for the success line. The room is expected clear here
    // (checked by the caller); a failed use is caught by the AwaitingUse timeout. Uses
    // the original place text so the item name matches (e.g. "token of the Lost City").
    private void UseNow()
    {
        _phase = Phase.AwaitingUse;
        int gen = _generation;
        _log?.Info("Tokens", $"token route to {_place}: using token (room clear), awaiting teleport");
        _send($"use {TokenCatalog.LookName(_displayPlace)}");
        _schedule(UseConfirmTimeoutMs, () =>
        {
            if (gen != _generation || _phase != Phase.AwaitingUse) return;
            _log?.Info("Tokens", $"token route to {_place}: no teleport confirmation — use likely failed; walking overland to destination");
            StandDownAndWalk();
        });
    }

    // TokenTracker.TokenUsed — a token use succeeded (the "You invoke the token…" line).
    public void OnTokenUsed(string usedPlace)
    {
        if (_phase != Phase.AwaitingUse) return;
        if (!string.Equals(TokenCatalog.NormalizePlace(usedPlace), _place, StringComparison.Ordinal)) return;

        _phase = Phase.AwaitingLanding;
        int gen = _generation;
        _log?.Info("Tokens", $"token route to {_place}: teleport confirmed, awaiting landing at {_landing}");
        _schedule(LandingConfirmTimeoutMs, () =>
        {
            if (gen != _generation || _phase != Phase.AwaitingLanding) return;
            _log?.Info("Tokens", $"token route to {_place}: landing room not confirmed in time — proceeding anyway");
            AfterLanding();
        });
    }

    // RoomTracker.StateChanged — entered/confirmed a room.
    public void OnRoomChanged(RoomKey? confirmedRoom)
    {
        if (confirmedRoom is not { } room) return;

        switch (_phase)
        {
            case Phase.WalkingToClear:
                if (_lastRoom is { } prev && prev.Equals(room)) return;   // same room, not a fresh entry
                _lastRoom = room;
                if (!_roomHasNpc())
                {
                    _log?.Info("Tokens", $"token route to {_place}: reached a monster-free room ({room}) — using the token");
                    _stopWalker();
                    UseNow();
                }
                // Else NPCs still here (hostiles auto-combat clears; friendlies we walk past) —
                // let the walk carry on to the next room.
                break;

            case Phase.AwaitingLanding:
                if (room.Equals(_landing))
                {
                    int gen = _generation;
                    _schedule(PostLandSettleMs, () =>
                    {
                        if (gen != _generation || _phase != Phase.AwaitingLanding) return;
                        _log?.Info("Tokens", $"token route to {_place}: landed at {room}");
                        AfterLanding();
                    });
                }
                break;
        }
    }

    // Landed at the token town. Solo → resume the walk. Leader → regroup first.
    private void AfterLanding()
    {
        if (_inParty() && _isLeader())
        {
            _regroupPending.Clear();
            foreach (string member in _membersToRegroup())
                if (GivenName(member) is { Length: > 0 } given)
                    _regroupPending.Add(given);

            if (_regroupPending.Count == 0) { ResumeWalk(); return; }

            _phase = Phase.Regrouping;
            _regroupRetries = 0;
            NextGeneration();   // fresh generation for the regroup timers
            _log?.Info("Tokens", $"token route to {_place}: landed — regrouping {_regroupPending.Count} member(s): {string.Join(", ", _regroupPending)}");
            SendRegroupWave();
            ScheduleRegroupTick();
            return;
        }
        ResumeWalk();
    }

    // Telepath every still-missing member `@do use <token>` so their client uses its
    // own copy of the token and gryphons in to us.
    private void SendRegroupWave()
    {
        string useCmd = $"use {TokenCatalog.LookName(_displayPlace)}";
        foreach (string given in _regroupPending)
            _send($"/{given} @do {useCmd}");
    }

    private void ScheduleRegroupTick()
    {
        int gen = _generation;
        _schedule(RegroupIntervalMs, () =>
        {
            if (gen != _generation || _phase != Phase.Regrouping) return;
            OnRegroupTick();
        });
    }

    private void OnRegroupTick()
    {
        if (_regroupPending.Count == 0) { ResumeWalk(); return; }
        if (_regroupRetries >= MaxRegroupRetries)
        {
            string missing = string.Join(", ", _regroupPending);
            string reason = $"Token regroup failed — {missing} didn't reach {_displayPlace}";
            _log?.Info("Tokens", $"token route to {_place}: {reason}; sitting for user action");
            StandDown();                 // sit — do NOT resume the walk
            RegroupFailed?.Invoke(reason);
            return;
        }
        _regroupRetries++;
        _log?.Info("Tokens", $"token route to {_place}: regroup retry {_regroupRetries}/{MaxRegroupRetries} — still waiting on {string.Join(", ", _regroupPending)}");
        SendRegroupWave();
        ScheduleRegroupTick();
    }

    // TokenTracker.MemberArrived — someone tokened into our (the leader's) landing room.
    public void OnMemberArrived(string arrivalName)
    {
        if (_phase != Phase.Regrouping) return;
        string given = GivenName(arrivalName);
        if (given.Length == 0 || !_regroupPending.Remove(given)) return;

        _log?.Info("Tokens", $"token route to {_place}: {given} arrived ({_regroupPending.Count} still out)");
        if (_regroupPending.Count == 0)
        {
            _log?.Info("Tokens", $"token route to {_place}: party fully regrouped — resuming walk to {_destination}");
            ResumeWalk();
        }
    }

    // Walker.Event — only meaningful while walking overland toward a clear room.
    public void OnWalkEvent(WalkEvent e)
    {
        if (_phase != Phase.WalkingToClear) return;
        switch (e.Kind)
        {
            case WalkEventKind.Finished:
                // Reached the destination without ever hitting a clear room to token in —
                // no token needed, the overland walk already got us there.
                _log?.Info("Tokens", $"token route to {_place}: reached destination overland before a clear room — token unused");
                StandDown();
                break;
            case WalkEventKind.Failed:
            case WalkEventKind.Stopped:
                // The walk failed or the user redirected it — abandon the token route.
                _log?.Info("Tokens", $"token route to {_place}: overland walk {e.Kind.ToString().ToLowerInvariant()} — abandoning token route");
                StandDown();
                break;
        }
    }

    private void ResumeWalk()
    {
        RoomKey dest = _destination;
        StandDown();
        _walkToDest(dest);
    }

    private void StandDownAndWalk()
    {
        RoomKey dest = _destination;
        StandDown();
        _walkToDest(dest);
    }

    private void StandDown()
    {
        _phase = Phase.Idle;
        _regroupPending.Clear();
        _regroupRetries = 0;
        NextGeneration();
    }

    private void NextGeneration() => _generation++;

    // "Boost" from "Boost the Bold" — party lines and @do address the given name only,
    // and arrival lines print it, so both sides match on it.
    private static string GivenName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        int sp = name.IndexOf(' ');
        return (sp < 0 ? name : name[..sp]).Trim();
    }
}
