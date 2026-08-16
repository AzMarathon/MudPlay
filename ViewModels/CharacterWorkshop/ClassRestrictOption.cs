using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One class row in the Quest editor's "Restrict to classes" checklist flyout. Number
// is the Classes-table id persisted onto QuestDefinition.ClassRestrict; IsSelected is
// the live checkbox state, watched by the owning QuestEditRowViewModel so it can refresh
// the dropdown's summary label as classes are ticked / unticked.
public sealed partial class ClassRestrictOption : ObservableObject
{
    public int Number { get; }
    public string Name { get; }

    [ObservableProperty] private bool _isSelected;

    public ClassRestrictOption(int number, string name, bool isSelected)
    {
        Number = number;
        Name = name;
        _isSelected = isSelected;
    }
}
