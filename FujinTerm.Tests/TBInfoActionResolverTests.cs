using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Pins TBInfoActionResolver's keyword enumeration: remoteaction lever/unlock
// keywords vs. the item-yielding room-action keywords (the Dwarven Mines
// "mine ore" gather commands, which the remoteaction filter deliberately skips).
public sealed class TBInfoActionResolverTests : IDisposable
{
    private readonly string _root;

    public TBInfoActionResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-action-resolver-tests-" + Path.GetRandomFileName());
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

    // The real Paradigm Copper Mine "mine ore" chain (TBInfo 1061): a checkitem
    // (pickaxe) + testskill gate ending in a `random` block that yields ore.
    private const string MineOreJson = """
        [ { "Number": 1061, "LinkTo": 0,
            "Action": "mine ore:checkitem 936 1608:adddelay 5:testskill strength 0 1062:random 1064\nmine vein:checkitem 936 1608:adddelay 5:testskill strength 0 1062:random 1064\nmine copper vein:checkitem 936 1608:adddelay 5:testskill strength 0 1062:random 1064\n",
            "Called From": "Room 6/1664" } ]
        """;

    [Fact]
    public void RoomActionKeywords_SurfaceMineGatherCommands()
    {
        TBInfoStore store = NewStore(MineOreJson);

        string[] kws = TBInfoActionResolver.EnumerateRoomActionKeywords(store, 1061).ToArray();

        Assert.Equal(new[] { "mine ore", "mine vein", "mine copper vein" }, kws);
    }

    [Fact]
    public void RoomActionKeywords_DirectGiveitem_AlsoSurfaces()
    {
        // A variant that gives the ore directly rather than via a random block.
        const string json = """
            [ { "Number": 200, "LinkTo": 0,
                "Action": "mine ore:checkitem 936 1608:giveitem 3533\ngather ore:checkitem 936 1608:giveitem 3533\n",
                "Called From": "Room 6/1900" } ]
            """;
        TBInfoStore store = NewStore(json);

        string[] kws = TBInfoActionResolver.EnumerateRoomActionKeywords(store, 200).ToArray();

        Assert.Equal(new[] { "mine ore", "gather ore" }, kws);
    }

    [Fact]
    public void RoomActionKeywords_SkipTeleportAndRemoteaction()
    {
        // A teleport line and a remoteaction line are surfaced by their own
        // resolvers, so the room-action enumerator must not double-list them —
        // only the giveitem line qualifies.
        const string json = """
            [ { "Number": 300, "LinkTo": 0,
                "Action": "go hole:teleport 487 2\npull lever:remoteaction 1012 1840 0 0\ntake gem:giveitem 555\n",
                "Called From": "Room 1/10" } ]
            """;
        TBInfoStore store = NewStore(json);

        string[] kws = TBInfoActionResolver.EnumerateRoomActionKeywords(store, 300).ToArray();

        Assert.Equal(new[] { "take gem" }, kws);
    }

    [Fact]
    public void RemoteActionKeywords_IgnoreMineGatherLines()
    {
        // The remoteaction enumerator (used for exit-unlock fallbacks) must NOT
        // pick up the mine/gather lines — they carry no remoteaction directive.
        TBInfoStore store = NewStore(MineOreJson);

        Assert.Empty(TBInfoActionResolver.EnumerateRemoteActionKeywords(store, 1061));
    }
}
