using System;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Tokens;

// Executes a picked token route for a SOLO character: use a Paradigm transport
// token to teleport to its town, then resume the walk from the landing to the
// destination. A token can only be used in a room with no monsters/NPCs, so if the
// current room isn't clear this walks the overland route toward the destination and
// uses the token at the first clear room it reaches (auto-combat clears hostiles on
// the way; friendly-NPC rooms are walked past) — a genuinely-shorter token route
// always surfaces a clear room before arrival. Never auto-started: the route picker
// only begins this on an explicit user pick.
//
// Party execution (confirm every member landed, re-send @do use, regroup or fail
// out) is a separate stage — TryBegin declines (returns false) when in a party so
// the caller walks the plain overland route instead.
//
// Delegate-injected (no live line stream) so it unit-tests without the app; driven
// by three events wired in AppServices: TokenTracker.TokenUsed (the teleport
// succeeded), Walker.Event (overland walk finished/failed), and
// RoomTracker.StateChanged (landed in / entered a room). Runs entirely on the UI
// thread (every source event is already marshalled there), so it needs no locking —
// same as ShortcutSourceCoordinator.
public sealed class TokenRouteCoordinator
{
    private enum Phase
    {
        Idle,
        WalkingToClear,  // NPCs were present at begin — walking overland, waiting for a clear room to use in.
        AwaitingUse,     // sent `use token of <place>`, waiting for the success line (TokenUsed).
        AwaitingLanding, // teleport succeeded, waiting for the room tracker to confirm the landing room.
    }

    // The teleport lands then the room display follows; give it this long to confirm
    // before resuming the walk from wherever we ended up anyway.
    private const int LandingConfirmTimeoutMs = 8000;
    // A `use` that fails (an NPC wandered in, gold fell short) prints no success line;
    // after this, treat it as failed and fall back to the plain overland walk.
    private const int UseConfirmTimeoutMs = 6000;
    // Let the landing room fully parse before planning the onward walk from it.
    private const int PostLandSettleMs = 700;

    private readonly Func<bool> _inParty;
    private readonly Func<bool> _roomHasNpc;
    private readonly Action<RoomKey> _walkToDest;
    private readonly Action _stopWalker;
    private readonly Action<string> _send;
    private readonly Action<int, Action> _schedule;
    private readonly LogService? _log;

    private Phase _phase = Phase.Idle;
    private string _place = "";
    private RoomKey _landing;
    private RoomKey _destination;
    private RoomKey? _lastRoom;
    // Invalidates a scheduled timeout when the phase moves on before it fires (a
    // DispatcherTimer callback still runs after we've advanced).
    private int _generation;

    public TokenRouteCoordinator(
        Func<bool> inParty,
        Func<bool> roomHasNpc,
        Action<RoomKey> walkToDest,
        Action stopWalker,
        Action<string> send,
        Action<int, Action> schedule,
        LogService? log = null)
    {
        _inParty = inParty ?? throw new ArgumentNullException(nameof(inParty));
        _roomHasNpc = roomHasNpc ?? throw new ArgumentNullException(nameof(roomHasNpc));
        _walkToDest = walkToDest ?? throw new ArgumentNullException(nameof(walkToDest));
        _stopWalker = stopWalker ?? throw new ArgumentNullException(nameof(stopWalker));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _log = log;
    }

    public bool Active => _phase != Phase.Idle;

    // Begin executing a picked token route. Returns false — so the caller walks the
    // plain overland route instead — when in a party (party regroup is a later stage).
    // Otherwise uses the token now if the room is clear, else walks overland and uses
    // it at the first clear room, and returns true.
    public bool TryBegin(string place, RoomKey landing, RoomKey destination)
    {
        if (string.IsNullOrWhiteSpace(place)) return false;
        if (_inParty())
        {
            _log?.Info("Tokens", $"token route to {place} declined — in a party (party regroup not yet supported); walking overland");
            return false;
        }

        _place = TokenCatalog.NormalizePlace(place);
        _landing = landing;
        _destination = destination;
        _lastRoom = null;
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
    // (checked by the caller); a failed use is caught by the AwaitingUse timeout.
    private void UseNow()
    {
        _phase = Phase.AwaitingUse;
        int gen = _generation;
        _log?.Info("Tokens", $"token route to {_place}: using token (room clear), awaiting teleport");
        _send($"use {TokenCatalog.LookName(_place)}");
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
            _log?.Info("Tokens", $"token route to {_place}: landing room not confirmed in time — resuming walk to {_destination} anyway");
            ResumeWalk();
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
                    // A monster-free room on the way — stop the overland walk and token from here.
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
                        _log?.Info("Tokens", $"token route to {_place}: landed at {room} — resuming walk to {_destination}");
                        ResumeWalk();
                    });
                }
                break;
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
        NextGeneration();
    }

    private void NextGeneration() => _generation++;
}
