using System.IO;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A board's count of deaths the standard list of arrival rooms doesn't allow:
// what marks a Stock board played on the standard data as one with edited rooms.
// Kept in the board's own bbs.json, read afresh on every ask as the client reads
// the active board.
public sealed class StandardListContradictionsTests : IDisposable
{
    private static readonly RoomKey Darkwood = new(1, 109);
    private static readonly RoomKey[] Listed = { new(1, 2189), new(1, 142) };

    private readonly string _boardName = "!mudplay-test-contradictions-" + Path.GetRandomFileName();
    private readonly List<LogEntry> _log = new();
    private readonly LogService _logService = new();
    private string? _set = "data-v1.11p";
    private int _saves;
    private Exception? _saveThrows;
    private BbsProfileStore _store = new();
    private ProfileService _profile;
    private StandardListContradictions _board;

    public StandardListContradictionsTests()
    {
        ScratchRoot.Require();
        _logService.EntryAdded += _log.Add;
        _store.Save(new BbsProfile { Name = _boardName, Host = "stock.example", Port = 23, Realms = { new RealmProfile { Name = "Stock" } } });
        (_profile, _board) = Start();
    }

    private readonly List<string> _otherBoards = new();

    public void Dispose()
    {
        foreach (string board in _otherBoards.Append(_boardName))
        {
            try { new BbsProfileStore().Delete(board); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort cleanup of a scratch board */ }
        }
    }

    private string NewBoard()
    {
        string name = "!mudplay-test-contradictions-" + Path.GetRandomFileName();
        _otherBoards.Add(name);
        _store.Save(new BbsProfile { Name = name, Host = "other.example", Port = 23, Realms = { new RealmProfile { Name = "Stock" } } });
        return name;
    }

    // A client started on the board: its character loaded there, and the board
    // and realm read from the file each time they are asked for.
    private (ProfileService, StandardListContradictions) Start()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile(onBbs: _boardName);
        StandardListContradictions board = new(
            () => profile.CurrentBbsName is { } name && _store.Get(name) is { } bbs && bbs.RealmFor(profile.Current?.Realm) is { } realm
                ? (bbs, realm)
                : null,
            () => _set,
            bbs =>
            {
                if (_saveThrows is not null) throw _saveThrows;
                _saves++;
                _store.Save(bbs);
            },
            _logService);
        return (profile, board);
    }

    private RealmProfile OnFile => new BbsProfileStore().Get(_boardName)!.Realms[0];

    private int Warnings => _log.Count(e => e.Severity == LogSeverity.Warn);

    private void Contradict(int arrivedIn = 500) => _board.Note(Darkwood, new RoomKey(1, arrivedIn), Listed);

    // One death can be the map's mistake (sure of a room, and wrong, in a grid of
    // same-named rooms). It is said, with the rooms, and changes nothing; the
    // second marks the board, in its file.
    [Fact]
    public void OneDeathIsSaid_AndTheSecondMarksTheBoard_InItsFile()
    {
        Assert.Equal((0, false), (_board.Count, _board.Marked));

        Contradict();

        Assert.Equal((1, false), (_board.Count, _board.Marked));
        Assert.Equal(1, OnFile.DeathsContradictingStandardArrivalRooms);
        LogEntry first = Assert.Single(_log, e => e.Severity == LogSeverity.Warn);
        Assert.Contains("A death seen in 1/109 ended in 1/500, where the standard list has the dead of that room arrive in 1/2189 or 1/142", first.Message);
        Assert.Contains("'data-v1.11p'", first.Message);
        Assert.Contains("1 more such death(s)", first.Message);

        Contradict(arrivedIn: 501);

        Assert.Equal((2, true), (_board.Count, _board.Marked));
        Assert.Equal(2, Warnings);
        Assert.Contains("ended in 1/501", _log[^1].Message);
        Assert.Contains("That is 2 such deaths on this board", _log[^1].Message);
        Assert.Contains("as on any board with edited rooms", _log[^1].Message);
        Assert.Equal(2, _saves);
        Assert.Equal((2, "data-v1.11p"), (OnFile.DeathsContradictingStandardArrivalRooms, OnFile.DeathsContradictingStandardArrivalRoomsOn));
    }

    // The mark is the board's, not the session's: the character loaded again, or
    // the client started afresh, finds it in the file.
    [Fact]
    public void TheMark_SurvivesAProfileReload_AndARestart()
    {
        Contradict();
        Contradict();

        _profile.LoadDefaultProfile(onBbs: _boardName);
        Assert.Equal((2, true), (_board.Count, _board.Marked));

        _store = new BbsProfileStore();
        (_profile, _board) = Start();
        Assert.Equal((2, true), (_board.Count, _board.Marked));
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
        Assert.Equal("the board's own data", OnFile.DeathsContradictingStandardArrivalRoomsOn);

        // Back on the first set, its count is gone too: one set at a time.
        _set = "data-v1.11p";
        Assert.Equal(0, _board.Count);
    }

    // A count written by a client before the set was kept with it stands for no
    // set, so it is not carried onto whichever set is in play.
    [Fact]
    public void ACountWithNoSetBesideIt_CountsForNothing()
    {
        BbsProfile bbs = _store.Get(_boardName)!;
        bbs.Realms[0].DeathsContradictingStandardArrivalRooms = 5;
        _store.Save(bbs);

        Assert.Equal((0, false), (_board.Count, _board.Marked));
    }

    // It is called from inside the room display's handling: a save that fails
    // throws nothing and is said once. The count is held for the session, so the
    // board is still marked by it, and goes into the file once it can be written.
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void ASaveThatFails_ThrowsNothing_IsSaidOnce_AndTheCountIsHeldUntilWritten(Type failure)
    {
        _saveThrows = (Exception)Activator.CreateInstance(failure, "the disk is full")!;

        Contradict();
        Contradict();

        Assert.Equal((2, true, true), (_board.Count, _board.Marked, _board.Unsaved));
        Assert.Equal(0, OnFile.DeathsContradictingStandardArrivalRooms);
        Assert.Single(_log, e => e.Severity == LogSeverity.Warn && e.Message.Contains("couldn't be written (the disk is full)"));
        Assert.Equal(0, _saves);

        // Still failing: tried again, and not said again.
        _board.RetryUnsaved();
        Assert.Single(_log, e => e.Severity == LogSeverity.Warn && e.Message.Contains("couldn't be written"));

        _saveThrows = null;
        _board.RetryUnsaved();

        Assert.Equal(2, OnFile.DeathsContradictingStandardArrivalRooms);
        Assert.Contains(_log, e => e.Severity == LogSeverity.Info && e.Message.Contains("(2) is written now"));
        Assert.Equal((2, true, false), (_board.Count, _board.Marked, _board.Unsaved));

        // Written, there is nothing more to try.
        _board.RetryUnsaved();
        Assert.Equal(1, _saves);
    }

    // The next death is counted on the held count, and takes it into the file.
    [Fact]
    public void AfterAFailedSave_TheNextDeathWritesTheHeldCountPlusOne()
    {
        _saveThrows = new IOException("the disk is full");
        Contradict();
        Assert.Equal((1, true), (_board.Count, _board.Unsaved));

        _saveThrows = null;
        Contradict();

        Assert.Equal((2, true, false), (_board.Count, _board.Marked, _board.Unsaved));
        Assert.Equal((2, "data-v1.11p"), (OnFile.DeathsContradictingStandardArrivalRooms, OnFile.DeathsContradictingStandardArrivalRoomsOn));
        Assert.Contains("That is 2 such deaths on this board", _log[^1].Message);
    }

    // A held count is the board's, realm's and set's it was counted on: it is not
    // written onto another board or against another set, and waits for its own.
    [Fact]
    public void AHeldCount_IsNotRetriedWhileAnotherBoardOrSetIsInPlay()
    {
        _saveThrows = new IOException("the disk is full");
        Contradict();
        Contradict();
        _saveThrows = null;

        _set = "the board's own data";
        _board.RetryUnsaved();
        Assert.Equal((0, false, false), (_board.Count, _board.Marked, _board.Unsaved));

        _set = "data-v1.11p";
        string other = NewBoard();
        _profile.LoadDefaultProfile(onBbs: other);
        _board.RetryUnsaved();
        Assert.Equal((0, false), (_board.Count, _board.Unsaved));
        Assert.Equal(0, _saves);
        Assert.Equal(0, OnFile.DeathsContradictingStandardArrivalRooms);
        Assert.Equal(0, new BbsProfileStore().Get(other)!.Realms[0].DeathsContradictingStandardArrivalRooms);

        _profile.LoadDefaultProfile(onBbs: _boardName);
        _board.RetryUnsaved();
        Assert.Equal(1, _saves);
        Assert.Equal(2, OnFile.DeathsContradictingStandardArrivalRooms);
    }

    // Each board's failed count is said once, by itself: a second board failing
    // too is said, and a retry that fails again is not.
    [Fact]
    public void FailedCounts_AreHeldAndSaidOncePerBoard()
    {
        _saveThrows = new IOException("the disk is full");
        Contradict();
        string other = NewBoard();
        _profile.LoadDefaultProfile(onBbs: other);
        Contradict();
        Contradict();
        _board.RetryUnsaved();

        Assert.Equal(2, _log.Count(e => e.Severity == LogSeverity.Warn && e.Message.Contains("couldn't be written")));
        Assert.Equal((2, true), (_board.Count, _board.Unsaved));

        _profile.LoadDefaultProfile(onBbs: _boardName);
        Assert.Equal((1, true), (_board.Count, _board.Unsaved));
    }

    // A board file that doesn't parse counts nothing and throws nothing into the
    // room display's handling; it is said once.
    [Fact]
    public void ABoardFileThatCantBeRead_CountsNothing_AndIsSaidOnce()
    {
        File.WriteAllText(AppPaths.BbsProfileFile(_boardName), "{ not json");

        Contradict();
        Contradict();
        _board.RetryUnsaved();

        Assert.Equal(0, _saves);
        Assert.Single(_log, e => e.Severity == LogSeverity.Warn && e.Message.Contains("couldn't be read"));
        Assert.DoesNotContain(_log, e => e.Message.Contains("A death seen in"));
    }

    [Fact]
    public void WithNoBoardOrNoSet_NothingIsCounted()
    {
        _set = null;
        Contradict();
        Assert.Equal(0, OnFile.DeathsContradictingStandardArrivalRooms);
        Assert.Equal(0, Warnings);

        StandardListContradictions noBoard = new(() => null, () => "data-v1.11p", _ => _saves++, _logService);
        noBoard.Note(Darkwood, new RoomKey(1, 500), Listed);
        Assert.Equal((0, false), (noBoard.Count, noBoard.Marked));
        Assert.Equal(0, _saves);
    }
}
