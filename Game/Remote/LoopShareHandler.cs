using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Sender side of `@loop send`: a player we've granted @loop asks for a copy of one of
// our saved loops. It's a two-step handshake so a typo can't ship the wrong loop:
//
//   @loop send <name>   → we best-match the name and offer it:
//                         {preparing to send: <loop>, yes to confirm, no to deny}
//   @loop send yes      → we send the offered loop as `@loopdata` lines (see
//                         LoopShareCodec), paced like an @roomba sync reply
//   @loop send no       → we drop the offer
//
// The offer is per requester and lapses after OfferLifetime, so a stale "yes" long
// after the question can't send anything. It rides @loop's MovePlayer grant — a
// player trusted to run our loops may also have a copy of one. The requester's
// client (LoopShareReceiver) reassembles and saves it.
public sealed class LoopShareHandler
{
    public const string SendVerb = "send";
    public const string DataToken = "@loopdata";

    private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(2);

    private readonly LoopManager _loops;
    private readonly PacedReplySender _sender;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly Dictionary<string, (Loop Loop, DateTimeOffset Expires)> _offers =
        new(StringComparer.OrdinalIgnoreCase);

    public LoopShareHandler(LoopManager loops, LogService? log = null,
        Action<TimeSpan, Action>? paceScheduler = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(loops);
        _loops = loops;
        _log = log;
        _sender = new PacedReplySender(paceScheduler, log);
        _now = clock ?? (() => DateTimeOffset.Now);
    }

    // Poke from the rate-limit-line watcher, same as the @roomba sync sender.
    public void NoteRateLimitClobber() => _sender.NoteClobber();

    // `@loop send <rest>` — rest is a loop name, "yes", or "no".
    public void OnSend(RemoteCommandContext ctx, string rest)
    {
        if (rest.Length == 0)
        {
            ctx.Reply("@loop send needs a loop name, then @loop send yes or no");
            return;
        }

        if (IsConfirm(rest)) { Confirm(ctx); return; }
        if (IsWord(rest, "no", "n")) { Deny(ctx); return; }

        if (MovePlayerHandler.ResolveSavedLoop(ctx, _loops, rest) is not { } loop) return;
        _offers[ctx.Sender] = (loop, _now() + OfferLifetime);
        _log?.Info("LoopShare", $"{ctx.Sender} asked for loop '{loop.Name}' — awaiting their yes/no.");
        ctx.Reply($"preparing to send: {loop.Name}, yes to confirm, no to deny");
    }

    private void Confirm(RemoteCommandContext ctx)
    {
        if (!TakeOffer(ctx.Sender, out Loop? loop))
        {
            ctx.Reply("no loop send pending — use @loop send <name> first");
            return;
        }

        IReadOnlyList<string> chunks = LoopShareCodec.Encode(loop);
        // A short id keeps two transfers to the same player (or two senders on a
        // shared channel) from mixing chunks on the receiving end.
        string id = Random.Shared.Next(0x1000, 0x10000).ToString("x4");
        List<string> lines = new(chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
            lines.Add($"{DataToken} {id} {i + 1}/{chunks.Count} {chunks[i]}");

        _log?.Info("LoopShare",
            $"sending loop '{loop.Name}' ({loop.Waypoints.Count} rooms) to {ctx.Sender} in {lines.Count} line(s).");
        ctx.Reply($"sending loop '{loop.Name}' ({loop.Waypoints.Count} rooms, {lines.Count} line{(lines.Count == 1 ? "" : "s")})");
        _sender.Enqueue(ctx.Reply, lines);
    }

    private void Deny(RemoteCommandContext ctx)
    {
        if (!TakeOffer(ctx.Sender, out Loop? loop))
        {
            ctx.Reply("no loop send pending");
            return;
        }
        _log?.Info("LoopShare", $"{ctx.Sender} declined loop '{loop.Name}'.");
        ctx.Reply($"loop send of '{loop.Name}' cancelled");
    }

    // Remove and return the requester's live offer; an expired one counts as none.
    private bool TakeOffer(string requester, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Loop? loop)
    {
        loop = null;
        if (!_offers.Remove(requester, out (Loop Loop, DateTimeOffset Expires) offer)) return false;
        if (_now() > offer.Expires) return false;
        loop = offer.Loop;
        return true;
    }

    // "yes" / "y" — also how the requester's client spots its own confirmation going
    // out (see LoopShareReceiver.NoteSendConfirmed).
    public static bool IsConfirm(string text) => IsWord(text, "yes", "y");

    private static bool IsWord(string text, string word, string abbrev) =>
        text.Equals(word, StringComparison.OrdinalIgnoreCase)
        || text.Equals(abbrev, StringComparison.OrdinalIgnoreCase);
}
