using System.IO;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A board's count of deaths the standard list of arrival rooms doesn't allow:
// what marks a Stock board played on the standard data as one with edited rooms.
public sealed class StandardListContradictionsTests
{
    private static readonly RoomKey Darkwood = new(1, 109);
    private static readonly RoomKey[] Listed = { new(1, 2189), new(1, 142) };

    private readonly RealmProfile _realm = new();
    private readonly List<LogEntry> _log = new();
    private readonly LogService _logService = new();
    private string? _set = "data-v1.11p";
    private int _saves;
    private Exception? _saveThrows;
    private readonly StandardListContradictions _board;

    public StandardListContradictionsTests()
    {
        _logService.EntryAdded += _log.Add;
        _board = new StandardListContradictions(() => _realm, () => _set, () =>
        {
            if (_saveThrows is not null) throw _saveThrows;
            _saves++;
        }, _logService);
    }

    private int Warnings => _log.Count(e => e.Severity == LogSeverity.Warn);

    private void Contradict(int arrivedIn = 500) => _board.Note(Darkwood, new RoomKey(1, arrivedIn), Listed);

    // One death can be the map's mistake (sure of a room, and wrong, in a grid of
    // same-named rooms). It is said, with the rooms, and changes nothing; the
    // second marks the board.
    [Fact]
    public void OneDeathIsSaid_AndTheSecondMarksTheBoard()
    {
        Assert.Equal((0, false), (_board.Count, _board.Marked));

        Contradict();

        Assert.Equal((1, false), (_board.Count, _board.Marked));
        LogEntry first = Assert.Single(_log, e => e.Severity == LogSeverity.Warn);
        Assert.Contains("A death seen in 1/109 ended in 1/500, where the standard list has the dead of that room arrive in 1/2189 or 1/142", first.Message);
        Assert.Contains("'data-v1.11p'", first.Message);
        Assert.Contains("1 more such death(s)", first.Message);

        Contradict(arrivedIn: 501);

        Assert.Equal((2, true), (_board.Count, _board.Marked));
        Assert.Equal(2, Warnings);
        Assert.Contains("ended in 1/501", _log[^1].Message);
        Assert.Contains("as on any board with edited rooms", _log[^1].Message);
        Assert.Equal(2, _saves);
        Assert.Equal("data-v1.11p", _realm.DeathsContradictingStandardArrivalRoomsOn);
    }

    // The count is this board's against that data set. Pointed at another set,
    // the realm starts afresh, and the first death there counts as one.
    [Fact]
    public void TheCountIsAgainstTheSet_AndAnotherSetStartsAfresh()
    {
        Contradict();
        Contradict();
        Assert.True(_board.Marked);

        _set = "the board's own data";
        Assert.Equal((0, false), (_board.Count, _board.Marked));

        Contradict();
        Assert.Equal((1, false), (_board.Count, _board.Marked));
        Assert.Equal("the board's own data", _realm.DeathsContradictingStandardArrivalRoomsOn);

        // Back on the first set, its count is gone too: one set at a time.
        _set = "data-v1.11p";
        Assert.Equal(0, _board.Count);
    }

    // A count written by a client before the set was kept with it stands for no
    // set, so it is not carried onto whichever set is in play.
    [Fact]
    public void ACountWithNoSetBesideIt_CountsForNothing()
    {
        _realm.DeathsContradictingStandardArrivalRooms = 5;

        Assert.Equal((0, false), (_board.Count, _board.Marked));
    }

    // It is called from inside the room display's handling: a save that fails
    // throws nothing, is said once, and leaves the count on the realm for the
    // board's next save.
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void ASaveThatFails_ThrowsNothing_IsSaidOnce_AndTheCountIsKept(Type failure)
    {
        _saveThrows = (Exception)Activator.CreateInstance(failure, "the disk is full")!;

        Contradict();
        Contradict();

        Assert.Equal((2, true), (_board.Count, _board.Marked));
        Assert.Equal(2, _realm.DeathsContradictingStandardArrivalRooms);
        Assert.Single(_log, e => e.Severity == LogSeverity.Warn && e.Message.Contains("couldn't be written (the disk is full)"));
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void WithNoBoardOrNoSet_NothingIsCounted()
    {
        _set = null;
        Contradict();
        Assert.Equal(0, _realm.DeathsContradictingStandardArrivalRooms);
        Assert.Equal(0, Warnings);

        StandardListContradictions noBoard = new(() => null, () => "data-v1.11p", () => _saves++, _logService);
        noBoard.Note(Darkwood, new RoomKey(1, 500), Listed);
        Assert.Equal((0, false), (noBoard.Count, noBoard.Marked));
        Assert.Equal(0, _saves);
    }
}
