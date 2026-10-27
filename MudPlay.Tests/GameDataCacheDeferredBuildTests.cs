using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The unnamed draft a launch comes up on chooses its game-data set without building
// anything from it: most such launches load a character on another set straight
// after, and the draft's build (room graph, loops, indexes; seconds) was thrown away
// (Hang-20261026-215238). Every store builds on ActiveSetChanged, so "nothing built"
// is "the event has not fired".
public sealed class GameDataCacheDeferredBuildTests : IDisposable
{
    private readonly string _root;

    public GameDataCacheDeferredBuildTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-gdc-defer-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private void SeedSet(string set)
    {
        string dir = Path.Combine(_root, set);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), "[{\"Number\":1,\"Name\":\"" + set + "\"}]");
    }

    private (GameDataCache Cache, List<string?> Built) NewCache()
    {
        SeedSet("draft");
        SeedSet("character");
        GameDataCache cache = new(_root);
        List<string?> built = new();
        cache.ActiveSetChanged += set => built.Add(set);
        return (cache, built);
    }

    [Fact]
    public void DeferredSwitch_NamesTheSet_ReadsItsTables_BuildsNothing()
    {
        (GameDataCache cache, List<string?> built) = NewCache();

        cache.SwitchSet("draft", deferBuild: true);

        Assert.Equal("draft", cache.ActiveSet);
        Assert.True(cache.BuildDeferred);
        Assert.Empty(built);
        // What reads a table on demand still gets the chosen set's.
        GameTable? items = cache.GetTable("Items");
        Assert.NotNull(items);
        Assert.True(items!.Rows[0].TryGetProperty("Name", out GameValue name));
        Assert.Equal("draft", name.GetString());
        Assert.Empty(built);
    }

    [Fact]
    public void FirstNeed_BuildsOnce()
    {
        (GameDataCache cache, List<string?> built) = NewCache();
        cache.SwitchSet("draft", deferBuild: true);

        Assert.True(cache.BuildDeferredSet("a test"));
        Assert.False(cache.BuildDeferredSet("a test"));

        Assert.Equal(new string?[] { "draft" }, built);
        Assert.False(cache.BuildDeferred);
    }

    [Fact]
    public void ACharacterOnAnotherSet_BuildsOnlyItsSet()
    {
        (GameDataCache cache, List<string?> built) = NewCache();
        cache.SwitchSet("draft", deferBuild: true);
        // Loading a character closes the draft first: the same set asked for again,
        // still put off.
        cache.SwitchSet("draft", deferBuild: true);

        cache.SwitchSet("character");

        Assert.Equal(new string?[] { "character" }, built);
        Assert.False(cache.BuildDeferred);
    }

    [Fact]
    public void ACharacterOnTheDraftsSet_BuildsIt()
    {
        (GameDataCache cache, List<string?> built) = NewCache();
        cache.SwitchSet("draft", deferBuild: true);

        cache.SwitchSet("draft");

        Assert.Equal(new string?[] { "draft" }, built);
        Assert.False(cache.BuildDeferred);
    }

    [Fact]
    public void NeverPutOff_WhileAnotherSetIsBuilt()
    {
        (GameDataCache cache, List<string?> built) = NewCache();
        cache.SwitchSet("character");

        // The stores still hold the character's set; they must hear it go.
        cache.SwitchSet("draft", deferBuild: true);

        Assert.Equal(new string?[] { "character", "draft" }, built);
        Assert.False(cache.BuildDeferred);
    }

    [Fact]
    public void AReload_BuildsAPutOffSet()
    {
        (GameDataCache cache, List<string?> built) = NewCache();
        cache.SwitchSet("draft", deferBuild: true);

        cache.ReloadActiveSet();

        Assert.Equal(new string?[] { "draft" }, built);
        Assert.False(cache.BuildDeferred);
        Assert.False(cache.BuildDeferredSet("a test"));
    }
}
