using Xunit;

namespace MudPlay.Tests;

// A BbsSectionViewModel opens on the first board in the data root, loads it, and
// writes every board it loaded back on Apply. Two test classes building one at
// the same time would each load the other's scratch board and save it again
// while its owner is deleting it. Classes that build the view model join this
// collection, which runs them one after another.
[CollectionDefinition(Name)]
public sealed class BbsSectionCollection
{
    public const string Name = "BbsSection";
}
