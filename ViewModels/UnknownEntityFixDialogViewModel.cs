using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Modeless dialog opened when the LogPane user double-clicks a row whose
// LogEntry.Source is "RoomClassifier" and whose LogEntry.Context carries the
// raw "Also Here:" wire line. Surfaces the offending entry name + the parsed
// list and lets the user pick a fix action.
//
// Game.Combat.RoomEntityClassifier opens the dialog when it emits the
// underlying Warn row, and the caller (App startup) wires the chosen action to
// the MonsterMessageStore (add-as-monster placeholder), the per-set
// FlavorPrefixStore (add-flavor-prefix), or the PlayerDatabase (player
// observation). The dialog itself doesn't touch the data layer — it only
// collects the user's intent, returns it via CloseRequested, and lets the
// caller commit.
//
// Result semantics: UnknownEntityFixAction.AddFlavorPrefix returns the
// dialog's UnknownEntity; the caller adds its leading word to the active set's
// shared flavor vocabulary, so every monster resolves that adjective.
// UnknownEntityFixAction.AddPlayerObservation returns the same string as the
// player name. Cancel returns null.
public sealed partial class UnknownEntityFixDialogViewModel : ObservableObject,
    IDialogViewModel<UnknownEntityFixResult?>
{
    public event Action<UnknownEntityFixResult?>? CloseRequested;

    public UnknownEntityFixDialogViewModel(string rawAlsoHereLine, string unknownEntity)
    {
        RawAlsoHereLine = rawAlsoHereLine ?? string.Empty;
        UnknownEntity   = unknownEntity   ?? string.Empty;
    }

    // The raw "Also Here:" line from the wire, copied verbatim for the user
    // to inspect.
    public string RawAlsoHereLine { get; }

    // The single comma-separated name segment the classifier could not
    // resolve to a Monster or a Player.
    public string UnknownEntity { get; }

    // Help text shown above the action buttons explaining what each action
    // does. Inline rather than per-button tooltips so the user sees both
    // options before picking.
    public string GuidanceText =>
        "If this is a new monster species the database doesn't have yet, add it as a " +
        "monster — a placeholder record lands in the catalogue immediately so the " +
        "classifier recognises it next time. If it's a flavor variant of an existing " +
        "species (e.g. \"stinking\" → giant rat), add it as a flavor prefix: the leading " +
        "word joins this realm's shared adjective vocabulary, so every monster resolves " +
        "it. If it's a player the database hasn't observed via `who`, add a placeholder " +
        "observation now and let the next `who` enrich it. Cancel leaves nothing changed.";

    [RelayCommand]
    private void AddAsMonster() =>
        CloseRequested?.Invoke(new UnknownEntityFixResult(
            UnknownEntityFixAction.AddAsMonster, UnknownEntity));

    [RelayCommand]
    private void AddFlavorPrefix() =>
        CloseRequested?.Invoke(new UnknownEntityFixResult(
            UnknownEntityFixAction.AddFlavorPrefix, UnknownEntity));

    [RelayCommand]
    private void AddPlayerObservation() =>
        CloseRequested?.Invoke(new UnknownEntityFixResult(
            UnknownEntityFixAction.AddPlayerObservation, UnknownEntity));

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

// Outcome of the user's pick in UnknownEntityFixDialogViewModel.
public enum UnknownEntityFixAction
{
    // Add the unknown name as a new bare-name Models.GameData.MonsterMessageRecord.
    // The classifier recognises the name on the next observation.
    AddAsMonster,

    // Treat the unknown name's leading word as a flavor adjective and add it to
    // the active set's shared flavor vocabulary (Services.FlavorPrefixStore).
    AddFlavorPrefix,

    // Treat the unknown name as a player and add a placeholder observation to
    // the per-BBS player database.
    AddPlayerObservation,
}

// Payload returned via UnknownEntityFixDialogViewModel.CloseRequested on a
// committed pick. Null result = Cancel. EntityName is the unknown name from
// the Also-Here line — stored so the caller doesn't have to thread it
// through state.
public sealed record UnknownEntityFixResult(
    UnknownEntityFixAction Action,
    string EntityName);
