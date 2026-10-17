using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class NeedsRegistryTests
{
    [Fact]
    public void Post_NewNeed_FiresEventAndIsOutstanding()
    {
        NeedsRegistry r = new();
        Need? fired = null;
        r.NeedPosted += n => fired = n;

        Need posted = r.Post(NeedKind.PathItem, "42", "PathItemDemandTracker");

        Assert.Equal(NeedKind.PathItem, posted.Kind);
        Assert.Equal("42", posted.Descriptor);
        Assert.Equal(posted, fired);
        Assert.Single(r.Outstanding(NeedKind.PathItem));
    }

    [Fact]
    public void Post_DuplicateKindDescriptor_DedupesAndDoesNotRefire()
    {
        NeedsRegistry r = new();
        int fires = 0;
        r.NeedPosted += _ => fires++;

        r.Post(NeedKind.PathItem, "42", "PathItemDemandTracker");
        r.Post(NeedKind.PathItem, "42", "SomeoneElse");

        Assert.Equal(1, fires);
        Assert.Single(r.Outstanding(NeedKind.PathItem));
    }

    [Fact]
    public void Resolve_RemovesNeed()
    {
        NeedsRegistry r = new();
        Need n = r.Post(NeedKind.PathItem, "42", "PathItemDemandTracker");

        Assert.True(r.Resolve(n));
        Assert.Empty(r.Outstanding(NeedKind.PathItem));
        Assert.False(r.Resolve(n));
    }

    [Fact]
    public void Clear_DropsAllNeeds()
    {
        NeedsRegistry r = new();
        r.Post(NeedKind.PathItem, "42", "PathItemDemandTracker");
        r.Post(NeedKind.PathItem, "100", "PathItemDemandTracker");

        r.Clear();

        Assert.Empty(r.Outstanding(NeedKind.PathItem));
    }
}
