using System.IO;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A leader crossing a teleport that splits the party relays the keyword to the
// followers and reforms afterwards; a spell that teleports the whole party moves
// them with the leader, so there's nothing to relay or reform.
public sealed class SpecialExitDispatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-exitdispatch-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private RoomTracker NewTracker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), "[]");
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return new RoomTracker(graph);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Teleport_RelaysAndReformsOnlyWhenThePartySplits(bool movesWholeParty, bool expectRelay)
    {
        RoomTracker tracker = NewTracker();
        RoomExit exit = new(new RoomKey(6, 1398), RoomExitHint.Teleport, "CMD teleport",
            TextCommands: new[] { "transport" }, MovesWholeParty: movesWholeParty);
        List<string> aux = new();
        List<string> moves = new();
        bool reformed = false;

        SpecialExitSend sent = SpecialExitDispatch.TrySendSynchronous(
            exit, Direction.Teleport, sourceRoom: null, tracker, recovery: null,
            emitMove: (b, _) => moves.Add(Encoding.Latin1.GetString(b)),
            writeAux: (b, _) => aux.Add(Encoding.Latin1.GetString(b)),
            teleportResolver: null, isLeaderWithFollowers: () => true, out _,
            onLeaderPartySplitTeleport: () => reformed = true);

        Assert.Equal(SpecialExitSend.Sent, sent);
        Assert.Equal(new[] { "transport\r" }, moves);
        Assert.Equal(expectRelay, aux.Contains(".@party transport\r"));
        Assert.Equal(expectRelay, reformed);
    }
}
