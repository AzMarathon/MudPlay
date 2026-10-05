using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace MudPlay.ViewModels.Navigation;

// The chips after the Navigation status line. Holds such as sneaking, looting or a
// settle beat often last a fraction of a second; folded into the status text they
// made it flash. Each hold, and each errand trip (bank, sell, training, going back
// for a member), gets its own chip instead: lit while the hold is in
// force, faded out over FadeTime once it ends, then removed. The engine is never
// slowed — only the display lingers.
public sealed class NavHoldChipStrip
{
    // Matches the opacity transition on Border.HoldChip in NavigationWindow.axaml.
    public static readonly TimeSpan FadeTime = TimeSpan.FromSeconds(3);

    private readonly Action<Action, TimeSpan> _schedule;

    public NavHoldChipStrip(Action<Action, TimeSpan>? schedule = null) =>
        _schedule = schedule ?? ((action, delay) => DispatcherTimer.RunOnce(action, delay));

    public ObservableCollection<NavHoldChip> Chips { get; } = [];

    // tips: hover text for the chips that have any, by label.
    public void Update(IReadOnlyList<(string Label, NavChipTone Tone)> active,
        IReadOnlyDictionary<string, Func<string?>>? tips = null)
    {
        foreach ((string label, NavChipTone tone) in active)
        {
            Func<string?>? tip = tips is not null && tips.TryGetValue(label, out Func<string?>? t) ? t : null;
            int at = IndexOf(label);
            if (at < 0)
                Chips.Add(new NavHoldChip(label, tone, tip));
            else if (Chips[at].IsCleared)
                // A fresh chip rather than un-clearing the old one: the view's fade
                // transition would otherwise fade it back in over two seconds.
                Chips[at] = new NavHoldChip(label, tone, tip);
        }

        foreach (NavHoldChip chip in Chips.ToArray())
        {
            if (chip.IsCleared || active.Any(a => a.Label == chip.Label)) continue;
            chip.IsCleared = true;
            // Removes this instance only: a chip lit again meanwhile is a new one.
            _schedule(() => Chips.Remove(chip), FadeTime);
        }
    }

    // The figures behind the chips' hover text moved on.
    public void RefreshTips()
    {
        foreach (NavHoldChip chip in Chips) chip.RefreshTip();
    }

    private int IndexOf(string label)
    {
        for (int i = 0; i < Chips.Count; i++)
            if (Chips[i].Label == label) return i;
        return -1;
    }
}
