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

        Need posted = r.Post(NeedKind.LightSource, "illu>=42", "AutoLightManager");

        Assert.Equal(NeedKind.LightSource, posted.Kind);
        Assert.Equal("illu>=42", posted.Descriptor);
        Assert.Equal(posted, fired);
        Assert.Single(r.Outstanding(NeedKind.LightSource));
    }

    [Fact]
    public void Post_DuplicateKindDescriptor_DedupesAndDoesNotRefire()
    {
        NeedsRegistry r = new();
        int fires = 0;
        r.NeedPosted += _ => fires++;

        r.Post(NeedKind.LightSource, "illu>=42", "AutoLightManager");
        r.Post(NeedKind.LightSource, "illu>=42", "SomeoneElse");

        Assert.Equal(1, fires);
        Assert.Single(r.Outstanding(NeedKind.LightSource));
    }

    [Fact]
    public void Resolve_RemovesNeed()
    {
        NeedsRegistry r = new();
        Need n = r.Post(NeedKind.LightSource, "illu>=42", "AutoLightManager");

        Assert.True(r.Resolve(n));
        Assert.Empty(r.Outstanding(NeedKind.LightSource));
        Assert.False(r.Resolve(n));
    }

    [Fact]
    public void Clear_DropsAllNeeds()
    {
        NeedsRegistry r = new();
        r.Post(NeedKind.LightSource, "illu>=42", "AutoLightManager");
        r.Post(NeedKind.LightSource, "illu>=100", "AutoLightManager");

        r.Clear();

        Assert.Empty(r.Outstanding(NeedKind.LightSource));
    }
}
