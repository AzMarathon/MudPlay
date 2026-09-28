using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Map;
using MudPlay.Game.Map.MpFile;

namespace MudPlay.ViewModels.Navigation;

// One translated row for the right pane, editable.
public sealed partial class MpTranslatedRowViewModel : ObservableObject
{
    private readonly string _originalRoomText;

    public MpTranslatedRowViewModel(MpTranslatedRow row, MpStep step, RoomGraphManager graph)
    {
        Index = row.Index;
        Step = step;
        Room = row.Room;
        _originalRoomText = row.Room?.ToString() ?? string.Empty;
        _roomText = _originalRoomText;
        RoomName = row.Room is { } k ? graph.GetRoom(k)?.Name ?? "(not in map data)" : "— untranslated —";
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
        if (!RoomKey.TryParseWire(RoomText.Trim(), out RoomKey k)) return null;
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
