using System.Text;
using MudPlay.Game.Inventory;

namespace MudPlay.Game.Combat;

// Sniffs outbound user commands for a hand-typed gear command (`eq`, `wear`, `wield`,
// `rem` and their short forms, with an item named) so the combat engine re-attacks at
// once when the game stops the fight for it.
//
// Putting gear on or taking it off mid-fight prints *Combat Off* on both realms
// (GAME_MECHANICS "Non-swing actions break combat (casting, equipping)"). The engine
// already re-attacks on that Off after a gear swap of its own
// (CombatManager.NoteGearSwapInterrupt); a typed one went unnoticed until the next
// round's lines, a round later (report paradigm-20261009-122342).
//
// Only the verb is recognised here. Whether a fight is on, and everything else that
// decides the re-attack, stays in CombatManager.NoteTypedGearCommand, so a gear
// command typed out of combat arms nothing.
//
// Hooked into the wire-send pipeline by MainWindowViewModel.SendUserInput, which
// hands it typed lines only: the client's own gear commands (the Equipment Manager,
// an item cast, the corpse recovery) come through EngineSendGate's wrapper and are
// left out, since each of those owns the re-attack after its own swap. Short payloads
// only: anything past ~64 bytes can't be a bare gear command.
public sealed class OutboundGearObserver
{
    private const int MaxBytes = 64;

    private readonly Action<string> _onGearCommand;

    public OutboundGearObserver(Action<string> onGearCommand)
    {
        ArgumentNullException.ThrowIfNull(onGearCommand);
        _onGearCommand = onGearCommand;
    }

    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaxBytes) return;
        string cmd = Encoding.Latin1.GetString(bytes)
            .TrimEnd('\r', '\n', '\0')
            .Trim();

        // The verb with nothing after it wears or removes nothing.
        int space = cmd.IndexOf(' ');
        if (space <= 0 || cmd[(space + 1)..].Trim().Length == 0) return;
        if (GearCommandVerbs.IsGearVerb(cmd[..space])) _onGearCommand(cmd);
    }
}
