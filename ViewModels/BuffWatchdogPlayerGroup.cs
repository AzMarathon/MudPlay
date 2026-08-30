using System.Collections.ObjectModel;

namespace MudPlay.ViewModels;

// One player's section in the Buff Watchdog timer view: a name header (yours, or a
// party member's) with the live buff-timer bars for every buff currently on that
// player. Self-cast and whole-party buffs sit under your own name; a single-target
// party buff sits under the member it's cast on.
public sealed class BuffWatchdogPlayerGroup
{
    public string PlayerName { get; }
    public ObservableCollection<BuffWatchdogRowViewModel> Rows { get; } = new();

    public BuffWatchdogPlayerGroup(string playerName) => PlayerName = playerName;
}
