using System.Text;
using MudPlay.Game.Map;

namespace MudPlay.Game.Combat;

// Sniffs outbound user commands for a manually-typed PHYSICAL attack (a swing / bash /
// smash / backstab) so the combat engine can treat it as a user override — the user is
// taking this round's attack, so the engine must not re-send its own auto-attack until
// the next round.
//
// The recognised verbs are attack-command prefixes MajorMUD honours: "a" / "at" /
// "att" (attack), "aa" and "bash" (both the bash command), "sma" / "smash", and "bs"
// (backstab). "sm" is not among them: smash starts at "sma", and the game takes "sm"
// for no command at all (GAME_MECHANICS "Command words and abbreviations"). Only the
// first whitespace-delimited token is the verb; the remainder is the target ("a giant
// rat").
//
// "bash <direction>" (or "aa <direction>", the same command) is the exception:
// MajorMUD reads a direction after `bash` as the door on that exit, not a monster, so
// it's no attack at all. Counting it as one made every door the walker bashed open
// hold the engine's attack for the round, and a buff cast in the next room then left
// the fight un-resumed (report paradigm-20261003-194358). The game only takes the door
// reading when the room has that exit; a monster targeted by a bare direction letter
// is rare enough to give up.
//
// Unlike casts, the combat engine's OWN physical attacks DO flow through this observer
// (SendAttack rides the same wrapped SendUserInput path). So the observer can't tell a
// manual swing from the engine's echo on its own — it forwards every recognised verb to
// CombatManager.NoteAttackCommandObserved, which consumes the engine's own send via a
// one-shot echo claim (set in SendAttack) and treats anything left as the user's.
//
// Hooked into the wire-send pipeline by MainWindowViewModel.SendUserInput. Short
// payloads only — anything past ~64 bytes can't be a bare attack command.
public sealed class OutboundAttackObserver
{
    private const int MaxBytes = 64;

    private static readonly HashSet<string> AttackVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "at", "att", "aa", "bash", "smash", "sma", "bs",
    };

    private readonly Action<string, string?> _onAttackCommand;

    public OutboundAttackObserver(Action<string, string?> onAttackCommand)
    {
        ArgumentNullException.ThrowIfNull(onAttackCommand);
        _onAttackCommand = onAttackCommand;
    }

    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaxBytes) return;
        string cmd = Encoding.Latin1.GetString(bytes)
            .TrimEnd('\r', '\n', '\0')
            .Trim();
        if (cmd.Length == 0) return;

        int space = cmd.IndexOf(' ');
        string verb = space >= 0 ? cmd[..space] : cmd;
        // The remainder is the target the user aimed at ("a giant rat" → "giant rat"),
        // needed to mark a manually-engaged neutral. null for a bare verb.
        string? target = space >= 0 ? cmd[(space + 1)..].Trim() : null;
        if (!AttackVerbs.Contains(verb)) return;
        if (IsDoorBash(verb, target)) return;
        _onAttackCommand(verb, string.IsNullOrEmpty(target) ? null : target);
    }

    private static bool IsDoorBash(string verb, string? target)
        => (verb.Equals("bash", StringComparison.OrdinalIgnoreCase)
                || verb.Equals("aa", StringComparison.OrdinalIgnoreCase))
            && DirectionExtensions.TryFromToken(target, out _);
}
