using MudPlay.Services;

namespace MudPlay.Game.Train;

// Leader side of a party train trip's roll call: who set out, the word to their
// clients that a trip is on, and at its end who it came back without.
//
// A member an exit turns away on the way (a trainer's room gated by level or
// class) must not stop the trip by asking to be fetched in the middle of it, and
// must not be forgotten either (user, 2026-10-10: "train trips should be excluded
// but if this happens because a gated trainer was picked that someone cant enter,
// the leader should realize that and pick them up when the training is done"). So
// a member that speaks `@ptrain` is told when the trip sets out and when it is
// over, and holds its own @comeback in between; and when the trip is over,
// whatever way it ended, everyone who set out and isn't following is named in the
// log, and the roll is handed on with how it ended. What is done about them is
// PartyComebackManager's (TrainTripEnded): a trip that ended by itself goes back
// for them, one the player took over leaves them to the player.
//
// Kept apart from PartyTrainCoordinator's trip itself so that this half can be
// driven without a trainer, a walk or a train.
public sealed class PartyTrainTripRoll
{
    private readonly Action<string> _send;
    private readonly Func<string, bool> _speaks;
    private readonly Func<IEnumerable<string>> _following;
    private readonly Action<IReadOnlyList<string>, bool>? _ended;
    private readonly LogService? _log;

    private List<string> _setOut = [];
    private List<string> _told = [];

    // send: a line for the wire. speaks: whether that member's client understands
    // `@ptrain`. following: the members following us right now. ended: told once
    // the trip is over, with who set out and whether the trip ended by itself.
    public PartyTrainTripRoll(
        Action<string> send,
        Func<string, bool> speaks,
        Func<IEnumerable<string>> following,
        Action<IReadOnlyList<string>, bool>? ended = null,
        LogService? log = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _speaks = speaks ?? throw new ArgumentNullException(nameof(speaks));
        _following = following ?? throw new ArgumentNullException(nameof(following));
        _ended = ended;
        _log = log;
    }

    // A trip is open from the moment the party sets out until Close.
    public bool IsOpen { get; private set; }

    // The members who set out on the trip in hand.
    public IReadOnlyList<string> SetOut => _setOut;

    // The trip sets out with the party as it stands.
    public void Open()
    {
        IsOpen = true;
        _setOut = _following().ToList();
        _told = _setOut.Where(_speaks).ToList();
        foreach (string name in _told) _send($"/{name} @ptrain trip on");
    }

    // The trip is over. Called after the engine it paused has been put back, so a
    // fetch has something to return to. byItself: it finished, or failed on its
    // own; false when it was taken out of its hands (a Stop, a run started over
    // it, its walk stopped under it).
    public void Close(bool byItself)
    {
        if (!IsOpen) return;
        IsOpen = false;
        foreach (string name in _told) _send($"/{name} @ptrain trip off");

        var here = new HashSet<string>(_following(), StringComparer.OrdinalIgnoreCase);
        List<string> setOut = _setOut;
        List<string> missing = setOut.Where(n => !here.Contains(n)).ToList();
        _setOut = [];
        _told = [];
        if (missing.Count > 0)
            _log?.Info(PartyTrainCoordinator.LogCategory,
                $"{string.Join(", ", missing)} set out and {(missing.Count == 1 ? "isn't" : "aren't")} following at the end of the trip{(byItself ? "" : ", which was taken out of its hands")}.");
        // Everyone who set out, not only who our list shows as gone: Stock tells a
        // leader nothing of a follower it drops, so the list may still show them.
        _ended?.Invoke(setOut, byItself);
    }
}
