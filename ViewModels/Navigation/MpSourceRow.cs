using System.Linq;
using MudPlay.Game.Map.MpFile;

namespace MudPlay.ViewModels.Navigation;

// One decoded step for the source pane.
public sealed class MpSourceRow
{
    public MpSourceRow(int number, MpStep step, string? roomsMdName)
    {
        Number = number;
        Hash = step.HashExits;
        RoomsMdName = roomsMdName ?? string.Empty;
        Action = step.Command ?? step.Compass?.ToString() ?? step.RawAction;
        Extra = string.Join("; ", step.PreActions.Concat(step.Note is { } n ? new[] { n } : Array.Empty<string>()));
        Flags = MpStepFlagText.Describe(step.Flags);
    }

    // The MegaMUD side of a row the user inserted: no step, every column empty.
    private MpSourceRow()
    {
        Hash = RoomsMdName = Action = Extra = Flags = string.Empty;
    }

    public static readonly MpSourceRow None = new();

    public int Number { get; }
    public string Hash { get; }
    public string RoomsMdName { get; }
    public string Action { get; }
    public string Extra { get; }
    public string Flags { get; }
}
