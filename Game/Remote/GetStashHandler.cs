using MudPlay.Game.Cash;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// @get-stash — search the room and take the coin the search shows, up to our own
// coin weight limits (Settings → Cash). A party leader moving a stash to a bank
// sends it so each member carries a load too: a search reveals hidden coin only to
// the one searching (GAME_MECHANICS "Hiding coin in a room (stashing)"), so every
// member has to search for itself, and one command does both steps.
//
// The stop is the leader's own (StashTransferRunner): pickup held at zero while the
// pile is read, then CashManager.CollectSurveyed for all of it, which the weight
// limits trim. The per-coin Collect / Ignore / Discard rules don't decide what is
// taken. The reply goes out only once the gets have landed: the leader takes it as
// "this member is loaded" and moves on when every member has answered. Replies are
// ASCII only — they ride the BBS wire.
public sealed class GetStashHandler : IDisposable
{
    private const string Command = "@get-stash";
    private const string LogCategory = "RemoteCmd";

    // How long the `sea` gets to answer, and the `get`s to land.
    public TimeSpan SurveyWindow { get; set; } = TimeSpan.FromSeconds(1.5);
    public TimeSpan CollectWindow { get; set; } = TimeSpan.FromSeconds(3);

    private readonly RemoteCommandManager _engine;
    private readonly Func<long> _onHandCopper;
    private readonly Action<string> _send;
    private readonly Action<TimeSpan, Action> _armTimer;
    private readonly Action<long?> _limitCollection;
    private readonly Func<long> _surveyedCopper;
    private readonly Action<long> _collectSurveyed;
    private readonly Action<bool> _forceAutoGetCash;
    private readonly Func<bool> _collectionInUse;
    private readonly LogService? _log;

    private bool _running;
    private int _session;
    private bool _disposed;

    public GetStashHandler(
        RemoteCommandManager engine,
        Func<long> onHandCopper,
        Action<string> send,
        Action<TimeSpan, Action> armTimer,
        // The collect engine's errand hooks, as StashTransferRunner takes them.
        Action<long?> limitCollection,
        Func<long> surveyedCopper,
        Action<long> collectSurveyed,
        Action<bool> forceAutoGetCash,
        // True while an errand of our own (a train-funding stop, a stash transfer)
        // holds the pickup ceiling; the two can't share it.
        Func<bool> collectionInUse,
        LogService? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _onHandCopper = onHandCopper ?? throw new ArgumentNullException(nameof(onHandCopper));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _armTimer = armTimer ?? throw new ArgumentNullException(nameof(armTimer));
        _limitCollection = limitCollection ?? throw new ArgumentNullException(nameof(limitCollection));
        _surveyedCopper = surveyedCopper ?? throw new ArgumentNullException(nameof(surveyedCopper));
        _collectSurveyed = collectSurveyed ?? throw new ArgumentNullException(nameof(collectSurveyed));
        _forceAutoGetCash = forceAutoGetCash ?? throw new ArgumentNullException(nameof(forceAutoGetCash));
        _collectionInUse = collectionInUse ?? throw new ArgumentNullException(nameof(collectionInUse));
        _log = log;

        if (!RemoteCommandCatalog.TryGetCategory(Command, out Models.GameData.PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{Command}'. Add it to the Map before registering.");
        _engine.RegisterHandler(Command, category, OnGetStash);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler(Command);
    }

    // Reset States, a disconnect: drop a stop that is under way.
    public void Cancel()
    {
        if (!_running) return;
        Finish();
    }

    private void OnGetStash(RemoteCommandContext ctx)
    {
        if (_running || _collectionInUse())
        {
            if (_engine.WarnOnDenial) ctx.Reply("busy with a coin errand of my own - try again shortly");
            return;
        }

        _running = true;
        int session = ++_session;
        _log?.Info(LogCategory, $"@get-stash from {ctx.Sender}: searching, then taking coin up to our weight limits");
        _forceAutoGetCash(true);
        _limitCollection(0);
        _send("sea");
        _armTimer(SurveyWindow, () => OnSurveyed(ctx, session));
    }

    private void OnSurveyed(RemoteCommandContext ctx, int session)
    {
        if (session != _session || !_running) return;

        long shown = Math.Max(0, _surveyedCopper());
        if (shown <= 0)
        {
            Finish();
            ctx.Reply("ok - found no coin here");
            return;
        }

        long before = _onHandCopper();
        _collectSurveyed(shown);
        _armTimer(CollectWindow, () =>
        {
            if (session != _session || !_running) return;
            long taken = Math.Max(0, _onHandCopper() - before);
            long left = Math.Max(0, shown - taken);
            Finish();
            _log?.Info(LogCategory, $"@get-stash: took {taken:N0} copper of {shown:N0} shown");
            ctx.Reply(taken > 0
                ? $"ok - took {CurrencyFormat.Full(taken)}" + (left > 0 ? $", left {CurrencyFormat.Full(left)}" : "")
                : "ok - at my coin weight limit, took nothing");
        });
    }

    private void Finish()
    {
        _running = false;
        _session++;
        _limitCollection(null);
        _forceAutoGetCash(false);
    }
}
