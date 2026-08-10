using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class TBInfoTeleportResolverTests : IDisposable
{
    private readonly string _root;

    public TBInfoTeleportResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-teleport-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private TBInfoStore NewStore(string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        return store;
    }

    [Fact]
    public void Resolve_SingleTeleportChain_ReturnsKeyword()
    {
        const string json = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go hole:message 767:teleport 487 2:message 768\n",
                "Called From": "Room 1/10" } ]
            """;
        TBInfoStore store = NewStore(json);

        string? kw = TBInfoTeleportResolver.Resolve(store, 100, new RoomKey(2, 487));
        Assert.Equal("go hole", kw);
    }

    [Fact]
    public void Resolve_MultipleAlternatives_ReturnsFirstMatching()
    {
        const string json = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go hole:message 767:teleport 487 2:message 768\nenter hole:message 767:teleport 487 2:message 768\ncrawl hole:message 767:teleport 487 2:message 768\n",
                "Called From": "Room 1/10" } ]
            """;
        TBInfoStore store = NewStore(json);

        string? kw = TBInfoTeleportResolver.Resolve(store, 100, new RoomKey(2, 487));
        Assert.Equal("go hole", kw);                              // first line wins
    }

    [Fact]
    public void Resolve_DestinationMismatch_ReturnsNull()
    {
        const string json = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go hole:teleport 999 9\n",
                "Called From": "Room 1/10" } ]
            """;
        TBInfoStore store = NewStore(json);

        Assert.Null(TBInfoTeleportResolver.Resolve(store, 100, new RoomKey(2, 487)));
    }

    [Fact]
    public void Resolve_NonTeleportEntry_ReturnsNull()
    {
        // Mini-game entry — has prices/random but no teleport directive.
        const string json = """
            [ { "Number": 997, "LinkTo": 0,
                "Action": "roll dice:price 10000 1560:random 998\n",
                "Called From": "Room 1/2" } ]
            """;
        TBInfoStore store = NewStore(json);

        Assert.Null(TBInfoTeleportResolver.Resolve(store, 997, new RoomKey(1, 998)));
    }

    [Fact]
    public void Resolve_UnknownCmd_ReturnsNull()
    {
        const string json = """ [] """;
        TBInfoStore store = NewStore(json);

        Assert.Null(TBInfoTeleportResolver.Resolve(store, 9999, new RoomKey(1, 1)));
    }

    [Fact]
    public void Resolve_CmdZero_ReturnsNull()
    {
        const string json = """ [] """;
        TBInfoStore store = NewStore(json);

        Assert.Null(TBInfoTeleportResolver.Resolve(store, 0, new RoomKey(1, 1)));
    }

    [Fact]
    public void Enumerate_MinLevelDirective_SurfacedOnTuple()
    {
        // Room 3/613 "go vortex" — minlevel 20 gate before the teleport.
        const string json = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go vortex:adddelay 5:minlevel 20 1220:message 1205:teleport 669 3:message 1221\n",
                "Called From": "Room 1/613" } ]
            """;
        TBInfoStore store = NewStore(json);

        (string keyword, RoomKey dest, int minLevel) =
            Assert.Single(TBInfoTeleportResolver.EnumerateTeleports(store, 100));
        Assert.Equal("go vortex", keyword);
        Assert.Equal(new RoomKey(3, 669), dest);
        Assert.Equal(20, minLevel);
    }

    [Fact]
    public void Enumerate_NoMinLevel_ReportsZero()
    {
        const string json = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go hole:message 767:teleport 487 2:message 768\n",
                "Called From": "Room 1/10" } ]
            """;
        TBInfoStore store = NewStore(json);

        (_, _, int minLevel) =
            Assert.Single(TBInfoTeleportResolver.EnumerateTeleports(store, 100));
        Assert.Equal(0, minLevel);
    }

    [Fact]
    public void EnumerateDestinations_FollowsLinkToChain_FindsCaptainDockTeleport()
    {
        // Paradigm-1.9.1 sea-captain dock (rooms 14/1812, 14/1813, 14/15000):
        // the CMD block is a non-keyword intro (a bold-black colour code in the
        // real data, a plain word here) that LinkTo's the effect block whose
        // keyword-less line carries `teleport 459 14`.
        const string json = """
            [ { "Number": 3371, "LinkTo": 3469, "Action": "intro\n\n",
                "Called From": "Room 14/1812" },
              { "Number": 3469, "LinkTo": 0,
                "Action": "message 3526:teleport 459 14:text 3379\n\n",
                "Called From": "Textblock #3371" } ]
            """;
        TBInfoStore store = NewStore(json);

        RoomKey dest = Assert.Single(
            TBInfoTeleportResolver.EnumerateTeleportDestinations(store, 3371));
        Assert.Equal(new RoomKey(14, 459), dest);
    }

    [Fact]
    public void EnumerateTeleports_KeywordResolver_IgnoresLinkToContinuation()
    {
        // The keyword-bearing resolver stays own-block-only on purpose: a
        // continuation teleport has no player keyword to type, so it must not be
        // minted into a walker edge (only the map-shift glyph/menu surfaces it).
        const string json = """
            [ { "Number": 3371, "LinkTo": 3469, "Action": "intro\n\n",
                "Called From": "Room 14/1812" },
              { "Number": 3469, "LinkTo": 0,
                "Action": "message 3526:teleport 459 14:text 3379\n\n",
                "Called From": "Textblock #3371" } ]
            """;
        TBInfoStore store = NewStore(json);

        Assert.Empty(TBInfoTeleportResolver.EnumerateTeleports(store, 3371));
    }

    [Fact]
    public void EnumerateDestinations_SelfReferentialLink_DoesNotLoop()
    {
        const string json = """
            [ { "Number": 100, "LinkTo": 100,
                "Action": "go hole:teleport 487 2\n", "Called From": "Room 1/10" } ]
            """;
        TBInfoStore store = NewStore(json);

        RoomKey dest = Assert.Single(
            TBInfoTeleportResolver.EnumerateTeleportDestinations(store, 100));
        Assert.Equal(new RoomKey(2, 487), dest);
    }
}
