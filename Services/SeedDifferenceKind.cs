namespace MudPlay.Services;

// How one message departs from the shipped seed (SeedDelta.Compare).
public enum SeedDifferenceKind
{
    // The user's copy of a seed record with edited text (so a new Id); the seed original
    // is removed underneath it.
    Edited,

    // A seed record whose editable fields (flags, links, and the like) the user changed.
    Override,

    // A record of the user's own with no seed counterpart.
    Added,

    // A seed record the user deleted, with no copy of theirs standing in for it.
    Removed,
}
