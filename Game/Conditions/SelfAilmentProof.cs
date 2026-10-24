using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Conditions;

// Outside proof that a latched ailment of our own is over when its wear-off line
// never came, or never comes at all (GAME_MECHANICS "Moving while blind"):
//   - a move that lands, shown by its room's display: a blind character's move shows
//     only "You are blind.", and a held or knocked-down one can't walk and isn't
//     dragged by a leader;
//   - the room shown in answer to a bare `look`: blind, a look also shows only
//     "You are blind.";
//   - a cure blindness landing on us: the cure prints its spell lines and no
//     wear-off, on both realms.
// Only those are confirmed. Other room displays (a peek, a teleport, the room shown
// on entering the game) clear nothing until the game's answer to a blind character
// there is known. Left latched, each ailment stood until Reset States: the walker sat
// "Paused by: Held" and the hold cure re-cast every window (report
// paradigm-20261003-161904); a cleric cast cure blindness on itself every 15 s while
// walking a loop it could plainly see (report CureBlindlessBugReport, 2026-10-22).
// A record that also carries another condition is left for blindness (ClearFlag's
// onlyWholly): sight proves nothing about a confusion riding the same record. A
// party member's chip is PartyAilmentTracker's (its WitnessCures); this is our own
// state only.
public sealed class SelfAilmentProof : IDisposable
{
    private readonly RoomTracker _tracker;
    private readonly ConditionTracker _conditions;
    private readonly MessageRouter _router;
    private readonly Func<IReadOnlyList<CureCastMatcher>> _readCureMatchers;
    private readonly Func<string?> _ownName;

    // Between the line after a bare `look`'s echo and the next prompt: the game's
    // answer to that look is being read.
    private bool _readingALookAnswer;
    private bool _disposed;

    public SelfAilmentProof(
        RoomTracker tracker,
        ConditionTracker conditions,
        MessageRouter router,
        Func<IReadOnlyList<CureCastMatcher>> readCureMatchers,
        Func<string?> ownName)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(readCureMatchers);
        ArgumentNullException.ThrowIfNull(ownName);
        _tracker = tracker;
        _conditions = conditions;
        _router = router;
        _readCureMatchers = readCureMatchers;
        _ownName = ownName;
        _tracker.MoveConfirmed += OnMoveConfirmed;
        _tracker.RoomDisplayRead += OnRoomDisplayRead;
        // The router hands its raw-line observers no chat line, so a gossip quoting a
        // cure can't clear anything.
        _router.LineDispatched += OnLine;
    }

    private void OnMoveConfirmed()
    {
        _conditions.ClearFlag(MessageFlags.MovementPrevented, "a move went through");
        _conditions.ClearFlag(MessageFlags.Blinded, "a move's room display was seen", onlyWholly: true);
    }

    private void OnRoomDisplayRead()
    {
        if (_readingALookAnswer)
            _conditions.ClearFlag(MessageFlags.Blinded, "a look showed the room", onlyWholly: true);
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine)
        {
            _readingALookAnswer = false;
            return;
        }
        if (_router.CommandEchoedBeforeLine is { } echoed) _readingALookAnswer = IsBareLook(echoed);
        if (!_conditions.IsBlinded) return;
        if (CuresUs(line.Text) is { } spell)
            _conditions.ClearFlag(MessageFlags.Blinded, $"{spell} landed on us", onlyWholly: true);
    }

    // `look` and the prefixes the game honours for it, with nothing after: a look
    // at the room, not a peek (`l n`) or a look at someone or something.
    private static bool IsBareLook(string command) =>
        command.Trim().ToLowerInvariant() is "l" or "lo" or "loo" or "look";

    // The blindness cure that the line shows landing on us, or null: our own cast
    // naming us ("You cast cure blindness on Francisco!") or another's cast at us
    // ("Kenwood casts cure blindness upon you!").
    private string? CuresUs(string text)
    {
        if (text.Length == 0) return null;
        string? full = _ownName();
        string given = CorpseRecoveryLines.GivenName(full);
        foreach (CureCastMatcher cm in _readCureMatchers())
        {
            if (cm.Ailment != MessageFlags.Blinded) continue;
            if (given.Length > 0
                && (cm.Caster.ConfirmsSpellTarget(text, cm.SpellName, given)
                    || cm.Caster.ConfirmsSpellTarget(text, cm.SpellName, full!)))
                return cm.SpellName;
            if (cm.Target is { } atUs
                && atUs.TryMatchCaptures(text, out Spells.MessageCaptures caps)
                && string.Equals(caps.Spell, cm.SpellName, StringComparison.OrdinalIgnoreCase))
                return cm.SpellName;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.MoveConfirmed -= OnMoveConfirmed;
        _tracker.RoomDisplayRead -= OnRoomDisplayRead;
        _router.LineDispatched -= OnLine;
    }
}
