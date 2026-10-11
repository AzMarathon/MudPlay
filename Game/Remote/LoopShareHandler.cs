using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Sender side of `@loop send`: a player we've granted @loop asks for a copy of one of
// our saved loops. It's a two-step handshake so a typo can't ship the wrong loop:
//
//   @loop send <name>   → we best-match the name and offer it:
//                         {preparing to send: <loop>, yes to confirm, no to deny (code <8 hex>)}
//   @loop send          → the same offer, for the loop we're running right now
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
    private readonly Func<Loop?> _runningLoop;
    private readonly LogService? _log;
    private readonly Dictionary<string, (Loop Loop, string Signature, DateTimeOffset Expires)> _offers =
        new(StringComparer.OrdinalIgnoreCase);

    public LoopShareHandler(LoopManager loops, LogService? log = null,
        Action<TimeSpan, Action>? paceScheduler = null, Func<DateTimeOffset>? clock = null,
        Func<Loop?>? runningLoop = null)
    {
        ArgumentNullException.ThrowIfNull(loops);
        _loops = loops;
        _log = log;
        _sender = new PacedReplySender(paceScheduler, log);
        _now = clock ?? (() => DateTimeOffset.Now);
        _runningLoop = runningLoop ?? (static () => null);
    }

    // Poke from the rate-limit-line watcher, same as the @roomba sync sender.
    public void NoteRateLimitClobber() => _sender.NoteClobber();

    // `@loop send <rest>` — rest is a loop name, "yes", "no", or empty for the loop
    // we're running.
    public void OnSend(RemoteCommandContext ctx, string rest)
    {
        Loop? loop;
        if (rest.Length == 0)
        {
            loop = _runningLoop();
            if (loop is null)
            {
                ctx.Refuse("not running a loop - @loop send <name> for a saved one");
                return;
            }
        }
        else
        {
            if (IsConfirm(rest)) { Confirm(ctx); return; }
            if (IsWord(rest, "no", "n")) { Deny(ctx); return; }
            loop = MovePlayerHandler.ResolveSavedLoop(ctx, _loops, rest);
            if (loop is null) return;
        }

        string signature = LoopSignature.Of(loop);
        _offers[ctx.Sender] = (loop, signature, _now() + OfferLifetime);
        _log?.Info("LoopShare",
            $"{ctx.Sender} asked for loop '{loop.Name}' [{LoopSignature.Short(signature)}] — awaiting their yes/no.");
        ctx.Reply(OfferLine(loop.Name, LoopSignature.Short(signature)));
    }

    // The offer, with the short code of the loop as this character runs it. The
    // asker's client keeps the code and checks the loop that arrives against it
    // (LoopShareReceiver), so both ends know the loop saved is the loop offered. A
    // client from before the code just shows the line: nothing reads it there.
    //
    // The code is LoopSignature.ShortLength hex characters, 32 bits. That is ample
    // for what it guards: one transfer arriving damaged in a way the packing did not
    // catch, or two offers getting mixed up. A wrong loop passing by chance is one
    // in four thousand million. It is not proof against someone forging a loop to
    // match a code, and is not meant to be: whether two loops are the same loop is
    // always decided on the whole signature.
    internal static string OfferLine(string loopName, string code) =>
        $"{OfferOpening}{loopName}{OfferClosing} (code {code})";

    private const string OfferOpening = "preparing to send: ";
    private const string OfferClosing = ", yes to confirm, no to deny";

    // Whether message is a `@loop send` offer, and the code it carries: null when it
    // has none, as from a client older than the code.
    internal static bool TryParseOffer(string message, out string? code)
    {
        code = null;
        string body = message.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[^1] == '}') body = body[1..^1].Trim();
        if (!body.StartsWith(OfferOpening, StringComparison.OrdinalIgnoreCase)) return false;
        int closing = body.LastIndexOf(OfferClosing, StringComparison.OrdinalIgnoreCase);
        if (closing < 0) return false;

        // What follows the closing words is the code, whole, or it is not one: a
        // line the game cut short must not leave half a code to compare with.
        string tail = body[(closing + OfferClosing.Length)..].Trim();
        const string open = "(code ";
        if (tail.Length > open.Length + 1 && tail.StartsWith(open, StringComparison.OrdinalIgnoreCase) && tail[^1] == ')')
        {
            string candidate = tail[open.Length..^1];
            if (candidate.Length >= LoopSignature.ShortLength && candidate.All(Uri.IsHexDigit))
                code = candidate.ToLowerInvariant();
        }
        return true;
    }

    private void Confirm(RemoteCommandContext ctx)
    {
        if (!TakeOffer(ctx.Sender, out Loop? loop, out string? offered))
        {
            ctx.Refuse("no loop send pending - use @loop send <name> first");
            return;
        }
        // The asker said yes to the loop as it was when offered, and checks what
        // arrives against that code. A loop edited since would be turned away there.
        if (LoopSignature.Of(loop) != offered)
        {
            _log?.Info("LoopShare",
                $"loop '{loop.Name}' changed since it was offered to {ctx.Sender} [{LoopSignature.Short(offered)} then, {LoopSignature.ShortOf(loop)} now] — not sent.");
            ctx.Refuse($"loop '{loop.Name}' changed since it was offered - ask for it again");
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
            $"sending loop '{loop.Name}' [{LoopSignature.ShortOf(loop)}] ({loop.Waypoints.Count} rooms) to {ctx.Sender} in {lines.Count} line(s).");
        ctx.Reply($"sending loop '{loop.Name}' ({loop.Waypoints.Count} rooms, {lines.Count} line{(lines.Count == 1 ? "" : "s")})");
        _sender.Enqueue(ctx.Reply, lines);
    }

    private void Deny(RemoteCommandContext ctx)
    {
        if (!TakeOffer(ctx.Sender, out Loop? loop, out _))
        {
            ctx.Refuse("no loop send pending");
            return;
        }
        _log?.Info("LoopShare", $"{ctx.Sender} declined loop '{loop.Name}'.");
        ctx.Reply($"loop send of '{loop.Name}' cancelled");
    }

    // Remove and return the requester's live offer; an expired one counts as none.
    // signature is the loop's when it was offered.
    private bool TakeOffer(string requester,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Loop? loop,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? signature)
    {
        loop = null;
        signature = null;
        if (!_offers.Remove(requester, out (Loop Loop, string Signature, DateTimeOffset Expires) offer)) return false;
        if (_now() > offer.Expires) return false;
        loop = offer.Loop;
        signature = offer.Signature;
        return true;
    }

    // "yes" / "y" — also how the requester's client spots its own confirmation going
    // out (see LoopShareReceiver.NoteSendConfirmed).
    public static bool IsConfirm(string text) => IsWord(text, "yes", "y");

    private static bool IsWord(string text, string word, string abbrev) =>
        text.Equals(word, StringComparison.OrdinalIgnoreCase)
        || text.Equals(abbrev, StringComparison.OrdinalIgnoreCase);
}
