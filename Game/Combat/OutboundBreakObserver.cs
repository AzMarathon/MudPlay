using System.Text;

namespace MudPlay.Game.Combat;

// Sniffs the user's own commands for a `break`, which holds the combat engine's
// attack on the monster it was fighting, and for the attack that lets it go again
// (CombatManager.NoteUserBreak / NoteUserAttack; user, 2026-10-10: "a manual break
// should hold attacking that target until the user types something to attack it").
//
// An attack is any attack word the game takes (AttackCommandWords) or the cast code
// of an attack spell, aimed at that monster or another. A direction after `bash` or
// `aa` is a door, as in OutboundAttackObserver.
//
// Hooked into the wire-send pipeline by MainWindowViewModel.SendUserInput, which
// hands it the user's own lines only: typed, or sent by a macro, trigger or event
// they set up (EngineSendGate.SendingUsersOwnCommand). An engine's `break` (a flee,
// a room attack broken off for a bystander, the combat toggle going off, `sys
// goto`) and an engine's attack never reach it. The line is read as it is sent, so
// a `break` typed ahead of the round that the game answers after it is the user's
// all the same. Short payloads only: anything past ~64 bytes is no bare command.
public sealed class OutboundBreakObserver
{
    private const int MaxBytes = 64;

    private readonly Func<string, bool> _isAttackSpell;
    private readonly Action<string> _onBreak;
    private readonly Action<string, string?> _onAttack;

    public OutboundBreakObserver(
        Func<string, bool> isAttackSpell, Action<string> onBreak, Action<string, string?> onAttack)
    {
        ArgumentNullException.ThrowIfNull(isAttackSpell);
        ArgumentNullException.ThrowIfNull(onBreak);
        ArgumentNullException.ThrowIfNull(onAttack);
        _isAttackSpell = isAttackSpell;
        _onBreak = onBreak;
        _onAttack = onAttack;
    }

    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaxBytes) return;
        string cmd = Encoding.Latin1.GetString(bytes)
            .TrimEnd('\r', '\n', '\0')
            .Trim();
        if (cmd.Length == 0) return;

        int space = cmd.IndexOf(' ');
        string word = space >= 0 ? cmd[..space] : cmd;
        string? target = space >= 0 ? cmd[(space + 1)..].Trim() : null;
        if (string.IsNullOrEmpty(target)) target = null;

        if (AttackCommandWords.IsBreak(word))
        {
            _onBreak(word);
            return;
        }

        bool attackWord = AttackCommandWords.IsAttack(word)
            && !AttackCommandWords.IsDoorBash(word, target);
        if (attackWord || _isAttackSpell(word)) _onAttack(word, target);
    }
}
