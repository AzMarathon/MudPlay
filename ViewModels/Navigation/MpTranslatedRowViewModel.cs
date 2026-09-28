using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Map;
using MudPlay.Game.Map.MpFile;

namespace MudPlay.ViewModels.Navigation;

// One line of the review table: a .mp step (Source) and our translation of it,
// editable. The room shows as its name and map/room; the "set room" box is empty
// unless the user has set one, and typing a map/room there overrides the
// translation on the next verify.
public sealed partial class MpTranslatedRowViewModel : ObservableObject
{
    private readonly string _originalRoomText;

    public MpTranslatedRowViewModel(MpTranslatedRow row, MpStep step, MpSourceRow source, RoomGraphManager graph)
    {
        Source = source;
        Index = row.Index;
        Step = step;
        Room = row.Room;
        _originalRoomText = row.Status == MpRowStatus.UserSet ? row.Room?.ToString() ?? string.Empty : string.Empty;
        _roomText = _originalRoomText;
        RoomName = row.Room is { } k ? $"{graph.GetRoom(k)?.Name ?? "(not in map data)"}  ·  {k}" : "— untranslated —";
        IsBlank = row.Status == MpRowStatus.Blank;
        StatusText = row.Status switch
        {
            MpRowStatus.Exact => "✓",
            MpRowStatus.HashDrift => "≈",
            MpRowStatus.Resynced => "↺",
            MpRowStatus.UserSet => "✎",
            _ => "✗",
        };
        StatusTip = row.Status switch
        {
            MpRowStatus.Exact => "Matches the recording",
            MpRowStatus.HashDrift => "Walked here, but this room's name or exits differ from the recording",
            MpRowStatus.Resynced => "Found by its hash near the last room we knew",
            MpRowStatus.UserSet => "Set by you",
            _ => "Couldn't be translated",
        } + (row.Note is { } n ? $" — {n}" : "");
        Note = row.Note ?? string.Empty;
        Dropped = MpStepFlagText.Dropped(step, row.PassageKnown);

        if (row.Room is { } room)
        {
            LoopWaypoint w = MpFileImporter.DefaultWaypoint(step, room, row.PassageKnown);
            _command = w.Command;
            _doNotRest = w.DoNotRest;
            _doNotAttack = w.DoNotAttack;
            _restHereHp = w.RestHereHp;
            _restHereMana = w.RestHereMana;
        }
    }

    // The step this row translates, shown on the same line of the review table.
    public MpSourceRow Source { get; }
    public int Index { get; }
    public int Number => Index + 1;
    public MpStep Step { get; }
    public RoomKey? Room { get; }
    public string RoomName { get; }
    public bool IsBlank { get; }
    public string StatusText { get; }
    public string StatusTip { get; }
    public string Note { get; }
    public string Dropped { get; }
    public bool HasDropped => Dropped.Length > 0;

    // Verify found no route onward from this room.
    [ObservableProperty] private bool _unreachable;

    // The room this row puts in the loop: one typed into the box, else the translation's.
    public RoomKey? EffectiveRoom =>
        RoomKey.TryParseWire(RoomText.Trim(), out RoomKey typed) ? typed : Room;

    [ObservableProperty] private string _roomText;
    [ObservableProperty] private string? _command;
    [ObservableProperty] private bool _doNotRest;
    [ObservableProperty] private bool _doNotAttack;
    [ObservableProperty] private bool _restHereHp;
    [ObservableProperty] private bool _restHereMana;

    public bool RoomEdited => RoomText.Trim() != _originalRoomText;

    public void CopyEditsFrom(MpTranslatedRowViewModel other)
    {
        Command = other.Command;
        DoNotRest = other.DoNotRest;
        DoNotAttack = other.DoNotAttack;
        RestHereHp = other.RestHereHp;
        RestHereMana = other.RestHereMana;
    }

    // Null for a row with no room — blanks are left out of the loop.
    public LoopWaypoint? ToWaypoint()
    {
        if (EffectiveRoom is not { } k) return null;
        string? cmd = string.IsNullOrWhiteSpace(Command) ? null : Command.Trim();
        return new LoopWaypoint(k, cmd, 0, DoNotRest, DoNotAttack) { RestHereHp = RestHereHp, RestHereMana = RestHereMana };
    }
}

// Plain-language names for a step's MegaMUD options, and what the translation drops.
internal static class MpStepFlagText
{
    public static string Describe(MpStepFlags f)
    {
        List<string> names = new();
        if (f.HasFlag(MpStepFlags.Dark)) names.Add("dark");
        if (f.HasFlag(MpStepFlags.PitchBlack)) names.Add("pitch black");
        if (f.HasFlag(MpStepFlags.RestHere)) names.Add("rest up here");
        if (f.HasFlag(MpStepFlags.DontRest)) names.Add("don't rest");
        if (f.HasFlag(MpStepFlags.NoAttack)) names.Add("don't attack");
        if (f.HasFlag(MpStepFlags.Stash)) names.Add("stash point");
        if (f.HasFlag(MpStepFlags.Disarm)) names.Add("disarm trap");
        if (f.HasFlag(MpStepFlags.CanPick)) names.Add("pick lock");
        if (f.HasFlag(MpStepFlags.Relearn)) names.Add("re-learn");
        if (f.HasFlag(MpStepFlags.Suspect)) names.Add("suspect");
        if (f.HasFlag(MpStepFlags.Cycled)) names.Add("cycled");
        int unknown = (int)f & ~0x87DF;
        if (unknown != 0) names.Add($"0x{unknown:X}");
        return string.Join(", ", names);
    }

    // What the loop won't carry from this step, and why.
    public static string Dropped(MpStep step, bool passageKnown)
    {
        List<string> dropped = new();
        MpStepFlags f = step.Flags;
        if ((f & (MpStepFlags.Dark | MpStepFlags.PitchBlack)) != 0) dropped.Add("light (handled from map data)");
        if (f.HasFlag(MpStepFlags.Disarm)) dropped.Add("disarm (traps come from map data)");
        if (f.HasFlag(MpStepFlags.CanPick)) dropped.Add("pick lock (doors come from map data)");
        if ((f & (MpStepFlags.Relearn | MpStepFlags.Suspect | MpStepFlags.Cycled)) != 0) dropped.Add("MegaMUD bookkeeping");
        if (step.PreActions.Count > 0) dropped.Add($"[{string.Join(", ", step.PreActions)}] (searches, keys and doors are handled on the way)");
        if (step.Command is { } c && passageKnown) dropped.Add($"'{c}' (our map has this passage)");
        else if (step.Command is { } s && MpFileImporter.IsSearch(s)) dropped.Add($"'{s}' (hidden exits are searched for automatically)");
        return string.Join("; ", dropped);
    }
}
