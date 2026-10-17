namespace MudPlay.ViewModels.GameData.Edit;

// One field's seed-vs-yours pair for the compare-with-seed dialog. FieldName is the
// field's label; ExistingValue and IncomingValue are the two sides (a null either side
// renders as a muted "(empty)"); Changed is true when they differ, and the view
// template highlights changed rows.
public sealed record FieldDiff(
    string FieldName,
    string? ExistingValue,
    string? IncomingValue,
    bool Changed);
