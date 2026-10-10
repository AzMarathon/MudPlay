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
// whatever way it ended, everyone who set out and isn't following is named, their
// request is taken as a party member's however long the trip ran, and the leader
// goes back for the ones it knows it left.
//
// Kept apart from PartyTrainCoordinator's trip itself so that this half can be
// driven without a trainer, a walk or a train.
public sealed class PartyTrainTripRoll
{
    private readonly Action<string> _send;
    private readonly Func<string, bool> _speaks;
    private readonly Func<IEnumerable<string>> _following;
    private readonly Action<string>? _expectComeback;
    private readonly Action? _fetchLeftBehind;
    private readonly LogService? _log;

    private List<string> _setOut = [];
    private List<string> _told = [];

    // send: a line for the wire. speaks: whether that member's client understands
    // `@ptrain`. following: the members following us right now. expectComeback:
    // told each member the trip came back without. fetchLeftBehind: told once the
    // trip is over, to go back for the members it is known to have left.
    public PartyTrainTripRoll(
        Action<string> send,
        Func<string, bool> speaks,
        Func<IEnumerable<string>> following,
        Action<string>? expectComeback = null,
        Action? fetchLeftBehind = null,
        LogService? log = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _speaks = speaks ?? throw new ArgumentNullException(nameof(speaks));
        _following = following ?? throw new ArgumentNullException(nameof(following));
        _expectComeback = expectComeback;
        _fetchLeftBehind = fetchLeftBehind;
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

    // The trip is over, however it ended. Called after the engine it paused has
    // been put back, so a fetch has something to return to.
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        foreach (string name in _told) _send($"/{name} @ptrain trip off");

        var here = new HashSet<string>(_following(), StringComparer.OrdinalIgnoreCase);
        List<string> missing = _setOut.Where(n => !here.Contains(n)).ToList();
        _setOut = [];
        _told = [];
        if (missing.Count > 0)
        {
            _log?.Info(PartyTrainCoordinator.LogCategory,
                $"{string.Join(", ", missing)} set out and didn't reach the end of the trip — going back for whoever was left on the way, and their @comeback is taken as a member's.");
            foreach (string name in missing) _expectComeback?.Invoke(name);
        }
        _fetchLeftBehind?.Invoke();
    }
}
