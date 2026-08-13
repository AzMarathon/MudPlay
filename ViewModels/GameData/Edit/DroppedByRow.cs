using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// One "dropped by" entry in an item's read-only MDB info: a monster that drops
// the item (with its drop-rate suffix, e.g. "Prismatic Dragon(10%)"), rendered
// as a clickable link that jumps the Game Data browser to that monster's record.
// The reverse of ItemLink (monster record → item link).
public sealed class DroppedByRow
{
    public string Label { get; }
    public ICommand Open { get; }

    public DroppedByRow(string label, int monsterNumber)
    {
        Label = label;
        Open = new RelayCommand(() => AppServices.Current.OpenMonsterGameData(monsterNumber));
    }
}
