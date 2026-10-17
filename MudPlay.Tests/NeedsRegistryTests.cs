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

        Need posted = r.Post("42", "PathItemDemandTracker");

        Assert.Equal("42", posted.Descriptor);
        Assert.Equal(posted, fired);
        Assert.Single(r.Outstanding());
    }

    [Fact]
    public void Post_DuplicateKindDescriptor_DedupesAndDoesNotRefire()
    {
        NeedsRegistry r = new();
        int fires = 0;
        r.NeedPosted += _ => fires++;

        r.Post("42", "PathItemDemandTracker");
        r.Post("42", "SomeoneElse");

        Assert.Equal(1, fires);
        Assert.Single(r.Outstanding());
    }

    [Fact]
    public void Resolve_RemovesNeed()
    {
        NeedsRegistry r = new();
        Need n = r.Post("42", "PathItemDemandTracker");

        Assert.True(r.Resolve(n));
        Assert.Empty(r.Outstanding());
        Assert.False(r.Resolve(n));
    }

    [Fact]
    public void Clear_DropsAllNeeds()
    {
        NeedsRegistry r = new();
        r.Post("42", "PathItemDemandTracker");
        r.Post("100", "PathItemDemandTracker");

        r.Clear();

        Assert.Empty(r.Outstanding());
    }
}
