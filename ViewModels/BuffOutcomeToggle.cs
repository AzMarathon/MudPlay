using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels;

// One outcome of a draw item (a card of the deck of cards) as a tick box in the
// Add / Edit buff dialog: ticked = keep it when it is drawn, unticked = draw again.
// Tip says what the buff applies.
public sealed partial class BuffOutcomeToggle : ObservableObject
{
    public int SpellNumber { get; }
    public string Label { get; }
    public string Tip { get; }

    [ObservableProperty] private bool _isChecked;

    private readonly System.Action _changed;

    public BuffOutcomeToggle(int spellNumber, string label, string tip, bool isChecked, System.Action changed)
    {
        SpellNumber = spellNumber;
        Label = label;
        Tip = tip;
        _isChecked = isChecked;
        _changed = changed;
    }

    partial void OnIsCheckedChanged(bool value) => _changed();
}
