using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// `@loop send`: the codec round-trips a loop through chat-safe chunks and refuses
// hostile payloads; the sender offers, then sends on yes / drops on no; the requester
// reassembles and saves without clobbering a loop of its own.
public sealed class LoopShareTests : IDisposable
{
    // Loops persist under the set's Loops/ folder in the real app data root (AppPaths
    // caches it at static init), so each test uses its own set name and deletes it.
    private readonly string _setName = "test-set-" + Guid.NewGuid().ToString("N")[..12];
    private DateTimeOffset _now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try
        {
            string setFolder = Path.Combine(AppPaths.GameDataRoot, _setName);
            if (Directory.Exists(setFolder)) Directory.Delete(setFolder, recursive: true);
        }
        catch { /* best-effort cleanup of the per-test set */ }
    }

    private LoopManager NewLoops()
    {
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), "[]");
        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        LoopManager loops = new(new BfsMapper(graph), graph);
        loops.LoadAll(_setName);
        return loops;
    }

    private static Loop SampleLoop(string name = "King's Road") => new(name, new[]
    {
        new LoopWaypoint(new RoomKey(1, 297), command: "ask barmaid ale", delayMs: 1500),
        new LoopWaypoint(new RoomKey(1, 298), doNotRest: true),
        new LoopWaypoint(new RoomKey(2, 14), doNotAttack: true),
    })
    { Notes = "watch the bridge", OnlyAttackInLairRooms = true, Favorite = true };

    // ----- Codec -----------------------------------------------------

    [Fact]
    public void Codec_RoundTripsTheRoute_ButNotFavourite()
    {
        Loop decoded = LoopShareCodec.Decode(LoopShareCodec.Encode(SampleLoop()));

        Assert.Equal("King's Road", decoded.Name);
        Assert.Equal("watch the bridge", decoded.Notes);
        Assert.True(decoded.OnlyAttackInLairRooms);
        Assert.False(decoded.Favorite);   // the receiver's own choice
        Assert.Equal(new[] { "1/297", "1/298", "2/14" }, decoded.Waypoints.Select(w => w.Room));
        Assert.Equal("ask barmaid ale", decoded.Waypoints[0].Command);
        Assert.Equal(1500, decoded.Waypoints[0].DelayMs);
        Assert.True(decoded.Waypoints[1].DoNotRest);
        Assert.True(decoded.Waypoints[2].DoNotAttack);
    }

    [Fact]
    public void Codec_BigLoop_SplitsIntoChatSizedChunks_AndReassembles()
    {
        Loop big = new("Grand tour", Enumerable.Range(1, 400)
            .Select(i => new LoopWaypoint(new RoomKey(1 + i % 7, i * 13), command: $"search {i}")));

        IReadOnlyList<string> chunks = LoopShareCodec.Encode(big);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, LoopShareCodec.MaxChunkChars));
        Assert.Equal(400, LoopShareCodec.Decode(chunks).Waypoints.Count);
    }

    [Fact]
    public void Codec_Garbage_IsAFormatException()
    {
        Assert.Throws<FormatException>(() => LoopShareCodec.Decode(new[] { "not*base64!" }));
        Assert.Throws<FormatException>(() => LoopShareCodec.Decode(new[] { "AAAA" }));
    }

    [Fact]
    public void Codec_OneRoomLoop_Refused()
    {
        string blob = Pack("{\"n\":\"x\",\"w\":[{\"r\":\"1/1\"}]}");
        Assert.Throws<FormatException>(() => LoopShareCodec.Decode(new[] { blob }));
    }

    [Fact]
    public void Codec_DeflateBomb_Refused()
    {
        string blob = Pack("{\"n\":\"" + new string('a', 2_000_000) + "\"}");
        Assert.True(blob.Length < 10_000);   // tiny on the wire…
        Assert.Throws<FormatException>(() => LoopShareCodec.Decode(new[] { blob }));   // …huge inflated
    }

    private static string Pack(string json)
    {
        using MemoryStream packed = new();
        using (DeflateStream d = new(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            d.Write(Encoding.UTF8.GetBytes(json));
        return System.Buffers.Text.Base64Url.EncodeToString(packed.ToArray());
    }

    // ----- Sender (LoopShareHandler) ---------------------------------

    private static RemoteCommandContext Ctx(string sender, List<string> replies) =>
        new(sender, "@loop", Array.Empty<string>(), "@loop send", RemoteChannel.Telepath, replies.Add);

    [Fact]
    public void Send_OffersTheBestMatch_ThenSendsOnYes()
    {
        LoopManager loops = NewLoops();
        loops.Save(SampleLoop());
        LoopShareHandler share = new(loops, clock: () => _now);
        List<string> replies = new();

        share.OnSend(Ctx("Raijin", replies), "kings road");
        Assert.Equal("preparing to send: King's Road, yes to confirm, no to deny", replies.Single());

        replies.Clear();
        share.OnSend(Ctx("Raijin", replies), "yes");
        Assert.StartsWith("sending loop 'King's Road' (3 rooms, ", replies[0]);
        List<string> data = replies.Skip(1).ToList();
        Assert.All(data, l => Assert.StartsWith(LoopShareHandler.DataToken + " ", l));

        // The data lines decode back to the loop.
        Loop decoded = LoopShareCodec.Decode(data.Select(l => l.Split(' ')[3]));
        Assert.Equal("King's Road", decoded.Name);
    }

    [Fact]
    public void Send_No_Cancels_AndALaterYesHasNothingPending()
    {
        LoopManager loops = NewLoops();
        loops.Save(SampleLoop());
        LoopShareHandler share = new(loops, clock: () => _now);
        List<string> replies = new();

        share.OnSend(Ctx("Raijin", replies), "king");
        share.OnSend(Ctx("Raijin", replies), "no");
        share.OnSend(Ctx("Raijin", replies), "yes");

        Assert.Equal("loop send of 'King's Road' cancelled", replies[1]);
        Assert.Equal("no loop send pending — use @loop send <name> first", replies[2]);
    }

    [Fact]
    public void Send_StaleOffer_Lapses()
    {
        LoopManager loops = NewLoops();
        loops.Save(SampleLoop());
        LoopShareHandler share = new(loops, clock: () => _now);
        List<string> replies = new();

        share.OnSend(Ctx("Raijin", replies), "king");
        _now += TimeSpan.FromMinutes(5);
        share.OnSend(Ctx("Raijin", replies), "yes");

        Assert.Equal("no loop send pending — use @loop send <name> first", replies[1]);
    }

    [Fact]
    public void Send_OfferIsPerRequester()
    {
        LoopManager loops = NewLoops();
        loops.Save(SampleLoop());
        LoopShareHandler share = new(loops, clock: () => _now);
        List<string> replies = new();

        share.OnSend(Ctx("Raijin", replies), "king");
        share.OnSend(Ctx("Suijin", replies), "yes");   // someone else can't confirm Raijin's offer

        Assert.Equal("no loop send pending — use @loop send <name> first", replies[1]);
    }

    [Fact]
    public void Send_MissingOrAmbiguousName_Replies()
    {
        LoopManager loops = NewLoops();
        loops.Save(SampleLoop("Sewer loop east"));
        loops.Save(SampleLoop("Sewer loop west"));
        LoopShareHandler share = new(loops, clock: () => _now);
        List<string> replies = new();

        share.OnSend(Ctx("Raijin", replies), "dragon");
        share.OnSend(Ctx("Raijin", replies), "sewer");
        share.OnSend(Ctx("Raijin", replies), "");

        Assert.Equal("no saved loop named 'dragon'", replies[0]);
        Assert.StartsWith("'sewer' matches 2 loops:", replies[1]);
        Assert.Equal("@loop send needs a loop name, then @loop send yes or no", replies[2]);
    }

    // ----- Receiver (LoopShareReceiver) ------------------------------

    private (LoopShareReceiver Receiver, LoopManager Loops, List<string> Notices) NewReceiver()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        LoopManager loops = NewLoops();
        List<string> notices = new();
        return (new LoopShareReceiver(new ChatRouter(router), loops, notices.Add, clock: () => _now), loops, notices);
    }

    private static List<string> DataLines(Loop loop, string id = "a1b2")
    {
        IReadOnlyList<string> chunks = LoopShareCodec.Encode(loop);
        return chunks.Select((c, i) => $"{{{LoopShareHandler.DataToken} {id} {i + 1}/{chunks.Count} {c}}}").ToList();
    }

    private static ChatLogEntry Tele(string sender, string msg) =>
        new(DateTimeOffset.UnixEpoch, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    [Fact]
    public void Receive_AfterOurYes_ReassemblesOutOfOrder_AndSaves()
    {
        var (receiver, loops, notices) = NewReceiver();
        Loop big = new("Grand tour", Enumerable.Range(1, 200)
            .Select(i => new LoopWaypoint(new RoomKey(1, i), command: $"search {i}")));
        List<string> lines = DataLines(big);
        Assert.True(lines.Count > 1);

        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in Enumerable.Reverse(lines)) receiver.Ingest(Tele("Raijin", line));

        Assert.Equal(200, loops.Get("Grand tour")!.Waypoints.Count);
        Assert.Equal("[Received loop 'Grand tour' 200 rooms from Raijin, saved in Loops]", notices.Single());
    }

    [Fact]
    public void Receive_WithoutOurYes_Ignored()
    {
        var (receiver, loops, notices) = NewReceiver();
        foreach (string line in DataLines(SampleLoop())) receiver.Ingest(Tele("Raijin", line));

        Assert.Empty(loops.Loops);
        Assert.Empty(notices);
    }

    [Fact]
    public void Receive_FromAPlayerWeDidntConfirmWith_Ignored()
    {
        var (receiver, loops, _) = NewReceiver();
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(SampleLoop())) receiver.Ingest(Tele("Mallory", line));

        Assert.Empty(loops.Loops);
    }

    [Fact]
    public void Receive_DifferentLoopUnderOurName_SavedAlongsideIt()
    {
        var (receiver, loops, notices) = NewReceiver();
        loops.Save(new Loop("King's Road", new[] { new RoomKey(9, 1), new RoomKey(9, 2) }));

        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(SampleLoop())) receiver.Ingest(Tele("Raijin", line));

        Assert.Equal("9/1", loops.Get("King's Road")!.Waypoints[0].Room);   // ours untouched
        Assert.Equal(3, loops.Get("King's Road (from Raijin)")!.Waypoints.Count);
        Assert.Equal("[Received loop 'King's Road' 3 rooms from Raijin, saved in Loops as 'King's Road (from Raijin)']", notices.Single());
    }

    [Fact]
    public void Receive_IdenticalLoopAlreadyHeld_NothingSaved()
    {
        var (receiver, loops, notices) = NewReceiver();
        loops.Save(SampleLoop());

        receiver.NoteSendConfirmed(null);   // confirmed on gangpath / say
        foreach (string line in DataLines(SampleLoop())) receiver.Ingest(Tele("Raijin", line));

        Assert.Single(loops.Loops);
        Assert.Equal("[Received loop 'King's Road' 3 rooms from Raijin, already saved in Loops]", notices.Single());
    }

    [Fact]
    public void Receive_OneTransferPerYes()
    {
        var (receiver, loops, _) = NewReceiver();
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(SampleLoop("First"))) receiver.Ingest(Tele("Raijin", line));
        foreach (string line in DataLines(SampleLoop("Second"), id: "c3d4")) receiver.Ingest(Tele("Raijin", line));

        Assert.NotNull(loops.Get("First"));
        Assert.Null(loops.Get("Second"));
    }
}
