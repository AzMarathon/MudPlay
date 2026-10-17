namespace MudPlay.Game.Map;

// A loop waiting to start by itself (set aside for the reconnect's restart, or
// loaded with Load) belongs to the character it was held for, and is dropped when
// another one takes its place (MovementController.DropHeldLoopsForProfileChange).
// The same character loaded again keeps it: a reconnect as that character still
// restarts the loop.
//
// A swap closes the loaded profile before it loads the next, and the close can't
// tell a swap from a plain close. So the close decides only once the load that may
// follow it has had its say: posted, and given up if a load came in between.
public sealed class HeldLoopsOnProfileSwap
{
    private readonly Func<MovementController?> _controller;
    private readonly Func<string?> _character;
    private readonly Action<string> _notice;
    private readonly Action<Action> _post;

    // The character the held loops were held for: the one last loaded.
    private string? _heldFor;
    // Bumped by every profile event, so a posted close a load has answered stands down.
    private int _events;

    // controller answers null until the movement controller is built (nothing is held
    // then). character names the loaded profile ("bbs/name"), or null with none loaded.
    public HeldLoopsOnProfileSwap(
        Func<MovementController?> controller, Func<string?> character, Action<string> notice, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(post);
        _controller = controller;
        _character = character;
        _notice = notice;
        _post = post;
    }

    public void OnProfileLoaded()
    {
        _events++;
        string? now = _character();
        if (_heldFor is not null && !string.Equals(_heldFor, now, StringComparison.OrdinalIgnoreCase))
            Drop("another character was loaded");
        _heldFor = now;
    }

    public void OnProfileClosed()
    {
        int close = ++_events;
        _post(() =>
        {
            if (close != _events || _character() is not null) return;
            Drop("the character was closed");
            _heldFor = null;
        });
    }

    private void Drop(string why)
    {
        if (_controller()?.DropHeldLoopsForProfileChange(why) is { } dropped)
            _notice($"[{dropped}: {why}]");
    }
}
