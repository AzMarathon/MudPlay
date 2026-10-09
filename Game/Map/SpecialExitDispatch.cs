using System;
using System.Collections.Generic;
using System.Text;

namespace MudPlay.Game.Map;

// Outcome of SpecialExitDispatch.TrySendSynchronous.
internal enum SpecialExitSend
{
    // The exit isn't a synchronous special exit. The caller handles it itself —
    // async door/hidden FSMs, or a plain cardinal move.
    NotHandled,

    // The helper emitted the crossing bytes and notified the tracker / recovery
    // gate. The caller does nothing further for this step.
    Sent,

    // The exit is a special exit but its game-data is invalid (see the out
    // reason). The caller should fail its walk / loop.
    Failed,
}

// Shared emission logic for the synchronous special exits — Text, Teleport, and
// same-room MultiActionHidden. Both AutoWalkManager (one-shot walks) and
// LoopRunner (loop circuits) cross these exits the same way, so the byte
// construction + tracker bookkeeping lives here once rather than being
// duplicated per engine.
//
// The two asynchronous special exits — door-open and hidden-exit reveal — are
// deliberately excluded: their FSMs (await the server's reply, then continue)
// differ per engine and stay owned by the caller. This helper only covers the
// cases that complete in a single send.
internal static class SpecialExitDispatch
{
    // A retreat's single step out of the room, told to the tracker and returned as
    // the bytes to send with a label for the log. A direction arrives with no exit
    // beside it, and a bare direction doesn't cross a text exit: the way back along
    // a trail can be one ("go path" in the northwest slot), and the game answers
    // the plain "nw" with "There is no exit in that direction!" (report
    // paradigm-20261004-201232, a flee that bonked that wall until it died). So
    // while the tracker knows the room, a text exit there goes out as its own
    // command. A tracker that has lost its place sends the direction as given.
    public static (byte[] Bytes, string Label) EncodeBacktrack(RoomTracker tracker, Direction direction)
    {
        if (tracker.State.Confidence == RoomConfidence.Confirmed
            && tracker.State.CurrentRoom is { } room
            && room.Exits.TryGetValue(direction, out RoomExit exit)
            && exit.Hint == RoomExitHint.Text && exit.TextCommands is { Count: > 0 } cmds)
        {
            tracker.NoteMoveSent(cmds[0], cardinal: direction);
            return (Encoding.Latin1.GetBytes(cmds[0] + "\r"),
                $"tier3 backtrack {direction} by its text exit '{cmds[0]}' → {exit.Target}");
        }
        tracker.NoteMoveSent(direction);
        return (AutoWalkManager.EncodeMove(direction), $"tier3 backtrack {direction}");
    }

    // Cross exit in direction when it is a synchronous special exit. Returns
    // NotHandled for ordinary passages and for the async door/hidden hints so
    // the caller can fall through to its own handling.
    //
    // sourceRoom is the current room (needed to resolve teleport keywords);
    // tracker and recovery are notified of the move / engine step (recovery may
    // be null). emitMove sends the move-completing bytes (the Text/Teleport
    // command, or the post-multi-action cardinal); callers fire their pre-move
    // hook here so stealth lands on the actual move, mirroring the walker.
    // writeAux sends fire-and-forget prerequisite bytes (multi-action commands,
    // the teleport party-relay) — no pre-move hook. teleportResolver maps
    // (source, dest) → keyword, or null when unwired. isLeaderWithFollowers is
    // true when the local character should relay the teleport keyword to
    // followers. onLeaderPartySplitTeleport (optional) fires right after a
    // leader crosses a party-splitting CMD teleport (chime-style): the relayed
    // `.@party <kw>` sends every follower through, but teleporting dissolves the
    // follow chain, so the party engine must re-invite to reform. failReason is
    // populated when the return value is Failed.
    public static SpecialExitSend TrySendSynchronous(
        RoomExit exit,
        Direction direction,
        Room? sourceRoom,
        RoomTracker tracker,
        EngineRecoveryGate? recovery,
        Action<byte[], string> emitMove,
        Action<byte[], string> writeAux,
        Func<RoomKey, RoomKey, string?>? teleportResolver,
        Func<bool>? isLeaderWithFollowers,
        out string? failReason,
        Action? onLeaderPartySplitTeleport = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(emitMove);
        ArgumentNullException.ThrowIfNull(writeAux);
        failReason = null;

        // MultiActionHidden — `(Hidden, Needs N Actions, ...)`. Execute the
        // prerequisite commands in StepNumber order, then send the cardinal.
        // Same-room actions only. Cross-room remote actions are pre-linearized
        // into an explicit walk/act/walk-back detour by RemoteActionPathExpander
        // (the point-to-point walker), so they never reach here as a single
        // MultiActionHidden step; the loop runner doesn't expand them, and the
        // walker fails such a route at plan time, so this stays a clear-fail
        // safety net. The reason is engine-neutral — it is reachable from either
        // caller and the old "loop circuits" wording misled on a plain walk.
        if (exit.Hint == RoomExitHint.MultiActionHidden && exit.MultiAction is { } maData)
        {
            // Already open — its actions were done by someone else, or it opened on
            // its own ("The exit to the <dir> just opened!"): just walk through.
            if (tracker.ShownOpenExits() is { } shown && shown.Contains(direction))
            {
                tracker.NoteMoveSent(direction);
                recovery?.NoteEngineStepSent(direction);
                emitMove(AutoWalkManager.EncodeMove(direction), $"move {direction} (multi-action exit already open)");
                return SpecialExitSend.Sent;
            }
            if (maData.HasRemoteActions)
            {
                failReason = "multi-action exit requires actions in another room, which can't be auto-crossed on this route";
                return SpecialExitSend.Failed;
            }
            if (maData.Actions.Count < maData.RequiredActionCount)
            {
                failReason = $"multi-action exit needs {maData.RequiredActionCount} action(s) but data has {maData.Actions.Count}";
                return SpecialExitSend.Failed;
            }

            foreach (ExitAction action in maData.Actions)
            {
                if (action.Commands.Count == 0) continue;
                string cmd = action.Commands[0];
                // Claim the echo first: several of these prerequisites begin with a
                // text-exit verb ("step tile", "climb rope", "cross plank"), so
                // without a claim the observer reads our own bytes as a hand-typed
                // move — which both enqueues a phantom and pauses navigation as a
                // user override.
                tracker.NoteAuxCommandSent(cmd);
                writeAux(Encoding.Latin1.GetBytes(cmd + "\r"), $"multi-action #{action.StepNumber}: '{cmd}'");
            }
            tracker.NoteMoveSent(direction);
            recovery?.NoteEngineStepSent(direction);
            emitMove(AutoWalkManager.EncodeMove(direction), $"move {direction} (post-multi-action)");
            return SpecialExitSend.Sent;
        }

        // Text exits — `(Text: cmd1, cmd2, ...)`. Any one alternative moves
        // the player (no follow-up cardinal). Send the first.
        if (exit.Hint == RoomExitHint.Text && exit.TextCommands is { Count: > 0 } cmds)
        {
            string textCmd = cmds[0];
            tracker.NoteMoveSent(textCmd, cardinal: direction);
            recovery?.NoteEngineStepSent(direction);
            emitMove(Encoding.Latin1.GetBytes(textCmd + "\r"), $"text-exit '{textCmd}' → {exit.Target}");
            return SpecialExitSend.Sent;
        }

        // Teleport exits. Two shapes reach here: a synthesised Direction.Teleport
        // edge carries its crossing keyword in TextCommands (baked in at graph
        // build so no lookup is needed), while a cardinal slot re-hinted Teleport
        // (a CMD teleport shadowing a Door/KeyLocked exit) carries none and
        // resolves (source, dest) → keyword through the resolver. Party-breaking:
        // a leader relays `.@party <kw>` so followers come along before the
        // leader teleports.
        if (exit.Hint == RoomExitHint.Teleport)
        {
            string? keyword = TeleportKeyword(exit, sourceRoom, teleportResolver);
            if (keyword is null)
            {
                failReason = "no teleport keyword resolved (TBInfo entry missing or not for this destination)";
                return SpecialExitSend.Failed;
            }

            // A spell that teleports the whole party moves the followers with us and
            // keeps the party formed, so there's nothing to relay or reform.
            bool leaderRelay = !exit.MovesWholeParty && isLeaderWithFollowers?.Invoke() == true;
            if (leaderRelay)
            {
                writeAux(Encoding.Latin1.GetBytes($".@party {keyword}\r"), $"teleport party-relay '.@party {keyword}'");
            }

            tracker.NoteMoveSent(keyword, cardinal: direction);
            recovery?.NoteEngineStepSent(direction);
            emitMove(Encoding.Latin1.GetBytes(keyword + "\r"), $"teleport '{keyword}' → {exit.Target}");

            // The teleport dissolved the follow chain even though every follower
            // was relayed through — reform the party once we land.
            if (leaderRelay) onLeaderPartySplitTeleport?.Invoke();
            return SpecialExitSend.Sent;
        }

        return SpecialExitSend.NotHandled;
    }

    // How many times a rolled reveal is sent again after the move behind it bonked,
    // before the step is handed to the engine's ordinary blocked-move handling. The
    // same patience a winch pull gets, for the same reason: each try is a stat roll.
    public const int RolledRevealRetryCap = 10;

    // The command of a same-room reveal that rolls (ExitAction.Rolled), when this
    // exit is one TrySendSynchronous opens by sending it; null for every other exit.
    // A winch is pulled, and pulled again, by WinchManager.
    public static string? RolledRevealCommand(RoomExit exit)
    {
        if (exit.Hint != RoomExitHint.MultiActionHidden || exit.MultiAction is not { } data) return null;
        if (data.HasRemoteActions || data.Actions.Count < data.RequiredActionCount) return null;
        if (WinchManager.IsWinchExit(exit)) return null;
        foreach (ExitAction action in data.Actions)
            if (action.Rolled && action.Commands.Count > 0) return action.Commands[0];
        return null;
    }

    private static string? TeleportKeyword(
        RoomExit exit, Room? sourceRoom, Func<RoomKey, RoomKey, string?>? teleportResolver)
        => exit.TextCommands is { Count: > 0 } teleCmds
            ? teleCmds[0]
            : (sourceRoom is not null && teleportResolver is not null)
                ? teleportResolver(sourceRoom.Key, exit.Target)
                : null;

    // The room commands crossing this exit puts on the wire, for a caller that has
    // to judge them before anything is sent: a teleport's keyword, and the
    // prerequisite commands of a same-room multi-action exit (a winch pull among
    // them) unless the exit already stands open. A text exit is an exit of the
    // room, not a command from its command block, so it yields nothing.
    public static IEnumerable<string> RoomCommandsFor(
        RoomExit exit, Direction direction, Room? sourceRoom, RoomTracker tracker,
        Func<RoomKey, RoomKey, string?>? teleportResolver)
    {
        if (exit.Hint == RoomExitHint.MultiActionHidden && exit.MultiAction is { } maData)
        {
            if (maData.HasRemoteActions) yield break;
            if (tracker.ShownOpenExits() is { } shown && shown.Contains(direction)) yield break;
            if (tracker.State.OpenDoorDirections is { } open && open.Contains(direction)) yield break;
            foreach (ExitAction action in maData.Actions)
                if (action.Commands.Count > 0) yield return action.Commands[0];
            yield break;
        }
        if (exit.Hint == RoomExitHint.Teleport
            && TeleportKeyword(exit, sourceRoom, teleportResolver) is { } keyword)
            yield return keyword;
    }
}
