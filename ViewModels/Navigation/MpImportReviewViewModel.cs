using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Game.Map.MpFile;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// The MegaMUD .mp import review: the left pane is the file decoded (header, path
// details, every step with its options and action), the right pane our translation,
// one row per step so the two can be checked line for line. A step that didn't
// translate is a blank row to fill in; typing a room into any row and pressing
// Re-walk re-translates the rest of the file from there. Accept saves the loop (and
// any stash rooms ticked); Reject closes without saving.
public sealed partial class MpImportReviewViewModel : ObservableObject, IDialogViewModel<Loop?>
{
    private readonly MpLoopFile _file;
    private readonly MpFileImporter _importer;
    private readonly RoomGraphManager _graph;
    private readonly LoopManager _loops;
    private readonly MovementFilter? _filter;
    private readonly LogService? _log;
    private IReadOnlyList<MpTranslation> _candidates;

    // Rooms the user has set by hand, by step, held across every re-walk. Clearing a
    // row's room releases it.
    private readonly Dictionary<int, RoomKey> _fixedRooms = new();

    public event Action<Loop?>? CloseRequested;

    public MpImportReviewViewModel(
        string path, MpLoopFile file, MegaMudRoomsFile? roomsMd,
        MpFileImporter importer, RoomGraphManager graph, LoopManager loops,
        MovementFilter? filter, LogService? log = null)
    {
        _file = file;
        _importer = importer;
        _graph = graph;
        _loops = loops;
        _filter = filter;
        _log = log;

        FileName = Path.GetFileName(path);
        Label = file.Label;
        Author = string.IsNullOrWhiteSpace(file.Author) ? "(none)" : file.Author;
        StartRoomText = DescribeHeader(file.Start, roomsMd);
        EndRoomText = file.End == file.Start ? "(same as start)" : DescribeHeader(file.End, roomsMd);
        PathDetails = new[]
        {
            $"Steps: {file.Steps.Count}" + (file.DeclaredStepCount >= 0 && file.DeclaredStepCount != file.Steps.Count
                ? $" (the file says {file.DeclaredStepCount})" : ""),
            $"Start room hash: {file.StartHashExits}" + (file.IsLoop ? "" : $" · end room hash: {file.EndHashExits}"),
            $"Gold needed: {file.Gold}",
            $"Item needed: {Or(file.RequiredItem)}",
            $"If the path fails, run: {Or(file.FailPath)}",
            $"When finished, run: {Or(file.SuccessPath)}",
            $"Use field: {Or(file.Use)}",
        };
        Problems = file.Problems;
        RoomsMdNote = roomsMd is null
            ? "No Rooms.md next to this file — rooms are shown by hash only."
            : $"Rooms.md found beside the file ({roomsMd.Rooms.Count} named rooms).";

        SourceRows = new ObservableCollection<MpSourceRow>(
            file.Steps.Select((s, i) => new MpSourceRow(i + 1, s, NameFromRoomsMd(roomsMd, s.HashExits))));

        LoopName = MpFileImporter.StripMapRoomSuffix(file.Label) is { Length: > 0 } clean
            ? clean
            : Path.GetFileNameWithoutExtension(path);
        Notes = BuildNotes(file);

        _candidates = file.IsLoop ? importer.TranslateCandidates(file) : Array.Empty<MpTranslation>();
        AnchorChoices = new ObservableCollection<MpAnchorChoice>(
            _candidates.Select(t => new MpAnchorChoice(t.Anchor, $"{t.Anchor} {graph.GetRoom(t.Anchor)?.Name} — {t.Summary}")));

        if (!file.IsLoop)
            Status = "This is a goto path, not a loop — it can be read here but not imported as a loop.";
        else if (_candidates.Count == 0)
            Status = $"No room in the active map data matches the start room (hash {file.StartHashExits}). "
                   + "Type its map/room in the first row and press Re-walk.";
        BuildRows(_candidates.Count > 0 ? _candidates[0] : null, preserveEdits: false);
        _selectedAnchor = AnchorChoices.FirstOrDefault();
        _log?.Info("MpImporter", $"reviewing {FileName}: {TranslationSummary}");
    }

    // ----- source pane ---------------------------------------------

    public string FileName { get; }
    public string Label { get; }
    public string Author { get; }
    public string StartRoomText { get; }
    public string EndRoomText { get; }
    public IReadOnlyList<string> PathDetails { get; }
    public IReadOnlyList<string> Problems { get; }
    public bool HasProblems => Problems.Count > 0;
    public string RoomsMdNote { get; }
    public ObservableCollection<MpSourceRow> SourceRows { get; }

    // ----- translation pane ----------------------------------------

    [ObservableProperty] private string _loopName = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _translationSummary = string.Empty;

    // Shared by both step lists so a row lines up with its source step.
    [ObservableProperty] private int _selectedIndex = -1;

    public ObservableCollection<MpAnchorChoice> AnchorChoices { get; }
    public bool HasAnchorChoice => AnchorChoices.Count > 1;

    [ObservableProperty] private MpAnchorChoice? _selectedAnchor;

    partial void OnSelectedAnchorChanged(MpAnchorChoice? value)
    {
        if (value is null) return;
        MpTranslation? t = _fixedRooms.Count > 0
            ? _importer.Translate(_file, value.Anchor, _fixedRooms)
            : _candidates.FirstOrDefault(c => c.Anchor.Equals(value.Anchor));
        if (t is not null) BuildRows(t, preserveEdits: true);
    }

    public ObservableCollection<MpTranslatedRowViewModel> Rows { get; } = new();

    // Stash points the loop marks (MegaMUD's per-step "Stash point"); ticking one
    // adds that room to your stash rooms when the loop is accepted.
    public ObservableCollection<MpStashChoice> StashChoices { get; } = new();
    public bool HasStashChoices => StashChoices.Count > 0;

    public bool CanAccept => _file.IsLoop;

    // Re-translate, holding every room typed into a row where it is.
    [RelayCommand]
    private void ReWalk()
    {
        Dictionary<int, RoomKey> updated = new(_fixedRooms);
        foreach (MpTranslatedRowViewModel row in Rows)
        {
            if (!row.RoomEdited) continue;
            string text = row.RoomText.Trim();
            if (text.Length == 0) { updated.Remove(row.Index); continue; }
            if (!RoomKey.TryParseWire(text, out RoomKey k) || _graph.GetRoom(k) is null)
            {
                Status = $"Row {row.Number}: '{row.RoomText}' isn't a room in the map data (use map/room, e.g. 1/2150).";
                return;
            }
            updated[row.Index] = k;
        }
        RoomKey anchor = updated.TryGetValue(0, out RoomKey first) ? first
            : SelectedAnchor?.Anchor ?? default;
        if (anchor.Equals(default(RoomKey)))
        {
            Status = "Set the first row's room (map/room) to start the walk.";
            return;
        }
        _fixedRooms.Clear();
        foreach ((int i, RoomKey k) in updated) _fixedRooms[i] = k;
        BuildRows(_importer.Translate(_file, anchor, _fixedRooms), preserveEdits: true);
        Status = _fixedRooms.Count == 0 ? "Re-walked." : $"Re-walked with {_fixedRooms.Count} room(s) you set held in place.";
        _log?.Info("MpImporter", $"{FileName}: re-walked with {_fixedRooms.Count} room(s) set by hand — {TranslationSummary}");
    }

    [RelayCommand]
    private void Accept()
    {
        if (!CanAccept) return;
        string name = LoopName.Trim();
        if (name.Length == 0) { Status = "Give the loop a name first."; return; }
        if (_loops.Get(name) is not null) { Status = $"A loop named '{name}' already exists — rename this one."; return; }

        List<LoopWaypoint> waypoints = MpFileImporter.Waypoints(Rows.Select(r => r.ToWaypoint()));
        if (waypoints.Count < 2) { Status = "The loop needs at least two rooms."; return; }

        Loop loop = new(name, waypoints) { Notes = Notes.Trim() };
        _loops.Save(loop);
        foreach (MpStashChoice s in StashChoices.Where(s => s.Add))
            _filter?.MarkStash(s.Room);
        int blanks = Rows.Count(r => r.IsBlank);
        _log?.Info("MpImporter",
            $"accepted {FileName} as '{name}': {waypoints.Count} waypoint(s)"
            + (blanks > 0 ? $", {blanks} step(s) left blank" : "")
            + $", {StashChoices.Count(s => s.Add)} stash room(s) added");
        CloseRequested?.Invoke(loop);
    }

    [RelayCommand]
    private void Reject()
    {
        _log?.Info("MpImporter", $"rejected {FileName}");
        CloseRequested?.Invoke(null);
    }

    // Rebuild the rows from a translation. preserveEdits keeps what the user typed or
    // ticked on a row (by step) when its room didn't change.
    private void BuildRows(MpTranslation? t, bool preserveEdits)
    {
        Dictionary<int, MpTranslatedRowViewModel> old = preserveEdits ? Rows.ToDictionary(r => r.Index) : new();
        Rows.Clear();
        for (int i = 0; i < _file.Steps.Count; i++)
        {
            MpTranslatedRow row = t?.Rows[i] ?? new MpTranslatedRow(i, null, MpRowStatus.Blank, "no start room yet", false);
            MpTranslatedRowViewModel vm = new(row, _file.Steps[i], _graph);
            if (old.TryGetValue(i, out MpTranslatedRowViewModel? prev) && prev.RoomText == vm.RoomText)
                vm.CopyEditsFrom(prev);
            Rows.Add(vm);
        }

        StashChoices.Clear();
        foreach (RoomKey k in Rows.Where(r => r.Step.Flags.HasFlag(MpStepFlags.Stash) && r.Room is not null)
                                  .Select(r => r.Room!.Value).Distinct())
            StashChoices.Add(new MpStashChoice(k, $"{k} {_graph.GetRoom(k)?.Name}"));
        OnPropertyChanged(nameof(HasStashChoices));

        TranslationSummary = t is null ? "Nothing translated yet." : $"Start {t.Anchor}: {t.Summary}.";
    }

    private static string DescribeHeader(MpHeaderRoom h, MegaMudRoomsFile? roomsMd)
    {
        string text = $"{h.Code} · {h.Group} · {h.Name}";
        if (roomsMd?.ByCode(h.Code) is not { } e) return text;
        List<string> bits = new();
        if (e.Options != MegaMudRoomOptions.None) bits.Add($"options: {DescribeOptions(e.Options)}");
        if (e.MinLevel > 0 || e.MaxLevel > 0) bits.Add($"levels {e.MinLevel}–{e.MaxLevel}");
        if (e.ClassId > 0) bits.Add($"class {e.ClassId}");
        return bits.Count == 0 ? text : $"{text} ({string.Join("; ", bits)})";
    }

    internal static string DescribeOptions(MegaMudRoomOptions o)
    {
        List<string> names = new();
        if (o.HasFlag(MegaMudRoomOptions.Shop)) names.Add("shop");
        if (o.HasFlag(MegaMudRoomOptions.Bank)) names.Add("bank");
        if (o.HasFlag(MegaMudRoomOptions.Trainer)) names.Add("trainer");
        if (o.HasFlag(MegaMudRoomOptions.StopBefore)) names.Add("stop before entering");
        if (o.HasFlag(MegaMudRoomOptions.Avoid)) names.Add("avoid");
        if (o.HasFlag(MegaMudRoomOptions.HideInGoto)) names.Add("hidden in goto list");
        if (o.HasFlag(MegaMudRoomOptions.CommonRooms)) names.Add("in common rooms");
        int unknown = (int)o & ~0xFE;
        if (unknown != 0) names.Add($"0x{unknown:X}");
        return string.Join(", ", names);
    }

    private static string? NameFromRoomsMd(MegaMudRoomsFile? roomsMd, string hash)
    {
        if (roomsMd is null) return null;
        List<MegaMudRoomEntry> hits = roomsMd.ByHash(hash).ToList();
        return hits.Count == 1 ? $"{hits[0].Code} {hits[0].Name}" : null;
    }

    // What the loop can't carry, written into its notes so it isn't lost.
    private static string BuildNotes(MpLoopFile file)
    {
        List<string> lines = new()
        {
            string.IsNullOrWhiteSpace(file.Author)
                ? $"Imported from MegaMUD .mp ({file.Start.Group}/{file.Start.Code})."
                : $"Imported from MegaMUD .mp by {file.Author} ({file.Start.Group}/{file.Start.Code}).",
        };
        if (file.Gold > 0) lines.Add($"MegaMUD: needs {file.Gold} gold.");
        if (file.RequiredItem.Length > 0) lines.Add($"MegaMUD: needs item '{file.RequiredItem}'.");
        if (file.FailPath.Length > 0) lines.Add($"MegaMUD: runs {file.FailPath} if the path fails.");
        if (file.SuccessPath.Length > 0) lines.Add($"MegaMUD: runs {file.SuccessPath} when finished.");
        return string.Join("\n", lines);
    }

    private static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "(none)" : s;
}

public sealed record MpAnchorChoice(RoomKey Anchor, string Text)
{
    public override string ToString() => Text;
}

public sealed partial class MpStashChoice : ObservableObject
{
    public MpStashChoice(RoomKey room, string text) { Room = room; Text = text; }
    public RoomKey Room { get; }
    public string Text { get; }
    [ObservableProperty] private bool _add;
}
