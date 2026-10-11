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
//   {@loopresult …}     ← the requester's client, once the loop has arrived: what
//                         became of it and its code for it (NoteChat). We show it
//                         and send nothing back.
//
// The offer is per requester and lapses after OfferLifetime, so a stale "yes" long
// after the question can't send anything. It rides @loop's MovePlayer grant — a
// player trusted to run our loops may also have a copy of one. The requester's
// client (LoopShareReceiver) reassembles and saves it.
public sealed class LoopShareHandler
{
    public const string SendVerb = "send";
    public const string DataToken = "@loopdata";

    // Leads the one line the asker's client sends back when the transfer is over:
    // {@loopresult <code> <what it did>}. A token and not a sentence so that no
    // client takes it for anything else: it is in braces, so no client reads it as a
    // command, and it does not open with a letter, so a client waiting on a
    // @version answer from that player does not take it for one.
    public const string ResultToken = "@loopresult";

    // Stands where the code goes when the asker's client could not read the loop
    // at all, and so has no code for it.
    public const string NoCode = "-";

    private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(2);

    // How long after a send the asker's answer is still taken as being about it. A
    // loop of a hundred lines is a minute and a half on the wire.
    private static readonly TimeSpan ResultAwaited = TimeSpan.FromMinutes(10);

    // The longest answer text shown: it is another player's client's words.
    private const int MaxOutcomeChars = 160;

    private readonly LoopManager _loops;
    private readonly PacedReplySender _sender;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Loop?> _runningLoop;
    private readonly LogService? _log;
    private readonly Dictionary<string, (Loop Loop, string Signature, DateTimeOffset Expires)> _offers =
        new(StringComparer.OrdinalIgnoreCase);

    // The last loop sent to each player, by given name, until their client answers.
    private readonly Dictionary<string, (string Name, string Signature, DateTimeOffset At)> _sent =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _notice;

    // notice shows the user what the asker's client answered about a loop we sent.
    public LoopShareHandler(LoopManager loops, LogService? log = null,
        Action<TimeSpan, Action>? paceScheduler = null, Func<DateTimeOffset>? clock = null,
        Func<Loop?>? runningLoop = null, Action<string>? notice = null)
    {
        ArgumentNullException.ThrowIfNull(loops);
        _loops = loops;
        _log = log;
        _sender = new PacedReplySender(paceScheduler, log);
        _now = clock ?? (() => DateTimeOffset.Now);
        _runningLoop = runningLoop ?? (static () => null);
        _notice = notice;
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
        _sent[Given(ctx.Sender)] = (loop.Name, offered, _now());
    }

    // ----- the asker's answer ------------------------------------------------

    // The line the asker's client sends back, once, when the transfer is over.
    internal static string ResultLine(string? code, string outcome) =>
        $"{ResultToken} {code ?? NoCode} {outcome}";

    // Read "@loopresult <code> <outcome>", in or out of its braces. code is null
    // when the asker's client had none to give.
    internal static bool TryParseResult(string message, out string? code, out string outcome)
    {
        code = null;
        outcome = string.Empty;
        string body = message.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[^1] == '}') body = body[1..^1].Trim();
        string[] parts = body.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[0].Equals(ResultToken, StringComparison.OrdinalIgnoreCase)) return false;
        if (parts[1] != NoCode)
        {
            if (parts[1].Length < LoopSignature.ShortLength || !parts[1].All(Uri.IsHexDigit)) return false;
            code = parts[1].ToLowerInvariant();
        }
        // Another client's words, shown on our terminal: nothing but plain text of
        // them, and not at any length.
        string text = new(parts[2].Where(c => !char.IsControl(c)).ToArray());
        outcome = text.Length <= MaxOutcomeChars ? text : text[..MaxOutcomeChars];
        return outcome.Length > 0;
    }

    // Every chat line comes by here. One that is the answer to a loop we sent that
    // player is shown and logged, with whether their code for the loop is ours: the
    // same code at both ends is the same loop at both ends. Nothing is sent back,
    // so two clients can never answer each other in a circle.
    public void NoteChat(ChatLogEntry e)
    {
        if (e.Channel is not (ChatChannel.TelepathIncoming or ChatChannel.Gangpath or ChatChannel.Local)) return;
        if (e.Speaker is not { Length: > 0 } asker) return;
        if (!TryParseResult(e.Message, out string? theirCode, out string outcome)) return;

        string given = Given(asker);
        if (!_sent.Remove(given, out (string Name, string Signature, DateTimeOffset At) sent)
            || _now() - sent.At > ResultAwaited)
        {
            _log?.Debug("LoopShare", $"ignoring {ResultToken} from {asker} — no loop was sent to them lately");
            return;
        }

        string ourCode = LoopSignature.Short(sent.Signature);
        string codes = theirCode is null ? string.Empty
            : sent.Signature.StartsWith(theirCode, StringComparison.Ordinal) ? $" (code {theirCode}, the same as sent)"
            : $" (code {theirCode}, NOT the code sent, {ourCode})";
        _log?.Info("LoopShare", $"{given} answered about loop '{sent.Name}' [{ourCode}]: {outcome}{codes}.");
        _notice?.Invoke($"[{given} on loop '{sent.Name}': {outcome}{codes}]");
    }

    private static string Given(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
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
