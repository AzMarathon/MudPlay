using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Requester side of `@loop send`: collects the `@loopdata <id> <i>/<n> <chunk>` lines
// a player sends after we answered their offer with `@loop send yes`, reassembles
// them (LoopShareCodec — a loop is only useful whole), and saves the loop to our
// catalogue.
//
// Lines are adopted only inside a window our OWN outbound `@loop send yes` opens
// (NoteSendConfirmed, from the outbound-chat watcher), and — when we confirmed by
// telepath, so we know who to — only from that player. The permission gate is on the
// sender, who only offered because they grant us @loop; we just confirm we asked.
//
// A received loop never overwrites one of ours: an identical loop already saved under
// that name is reported as already had, and a different one lands as
// "<name> (from <sender>)".
public sealed class LoopShareReceiver : IDisposable
{
    // Long enough for a big loop's paced lines (~800ms apart) and a slow sender.
    private static readonly TimeSpan AcceptWindow = TimeSpan.FromMinutes(2);

    // A loop worth sending fits well under this; the cap bounds what a stream of
    // bogus lines can make us buffer.
    private const int MaxChunks = 100;

    private readonly ChatRouter _chat;
    private readonly LoopManager _loops;
    private readonly Action<string> _notice;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly Dictionary<(string Sender, string Id), string?[]> _partial = new();
    private DateTimeOffset _acceptUntil = DateTimeOffset.MinValue;
    private string? _expectedSender;
    private bool _disposed;

    public LoopShareReceiver(ChatRouter chat, LoopManager loops, Action<string> notice,
        LogService? log = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(notice);
        _chat = chat;
        _loops = loops;
        _notice = notice;
        _log = log;
        _now = clock ?? (() => DateTimeOffset.Now);
        _chat.EntryClassified += Ingest;
    }

    // We just sent `@loop send yes`. sender is the telepath recipient, or null when we
    // confirmed on gangpath / say (then any player on that channel may answer).
    public void NoteSendConfirmed(string? sender)
    {
        _acceptUntil = _now() + AcceptWindow;
        _expectedSender = string.IsNullOrWhiteSpace(sender) ? null : sender;
        _partial.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= Ingest;
    }

    internal void Ingest(ChatLogEntry e)
    {
        if (e.Channel is not (ChatChannel.TelepathIncoming or ChatChannel.Gangpath or ChatChannel.Local))
            return;
        if (e.Speaker is not { Length: > 0 } sender) return;
        if (!TryParse(e.Message, out string id, out int index, out int count, out string chunk)) return;
        if (_now() > _acceptUntil)
        {
            _log?.Debug("LoopShare", $"ignoring @loopdata from {sender} — no @loop send yes sent recently");
            return;
        }
        if (_expectedSender is { } expected && !SameGiven(expected, sender))
        {
            _log?.Debug("LoopShare", $"ignoring @loopdata from {sender} — we confirmed with {expected}");
            return;
        }

        (string, string) key = (sender, id);
        if (!_partial.TryGetValue(key, out string?[]? parts))
        {
            parts = new string?[count];
            _partial[key] = parts;
        }
        else if (parts.Length != count)
        {
            _log?.Warn("LoopShare", $"discarding @loopdata from {sender} — its line count changed mid-transfer");
            _partial.Remove(key);
            return;
        }
        parts[index - 1] = chunk;
        if (parts.Any(p => p is null)) return;

        _partial.Remove(key);
        _acceptUntil = DateTimeOffset.MinValue;   // one transfer per yes
        Loop loop;
        try { loop = LoopShareCodec.Decode(parts!); }
        catch (FormatException ex)
        {
            _log?.Warn("LoopShare", $"loop from {sender} couldn't be read: {ex.Message}");
            _notice($"[Loop from {sender} arrived damaged, not saved]");
            return;
        }
        Save(loop, sender);
    }

    private void Save(Loop loop, string sender)
    {
        if (_loops.SetName is null)
        {
            _log?.Warn("LoopShare", $"loop '{loop.Name}' from {sender} not saved — no game-data set is active");
            _notice($"[Received loop '{loop.Name}' {loop.Waypoints.Count} rooms from {sender}, not saved: no game data set is loaded]");
            return;
        }

        if (_loops.Get(loop.Name) is { } existing && SameRoute(existing, loop))
        {
            _log?.Info("LoopShare", $"loop '{loop.Name}' from {sender} matches ours — nothing saved.");
            _notice($"[Received loop '{loop.Name}' {loop.Waypoints.Count} rooms from {sender}, already saved in {Location(existing)}]");
            return;
        }

        string original = loop.Name;
        loop.Name = FreeName(original, sender);
        _loops.Save(loop);
        _log?.Info("LoopShare",
            $"saved loop '{loop.Name}' ({loop.Waypoints.Count} rooms) received from {sender}.");
        _notice(loop.Name == original
            ? $"[Received loop '{loop.Name}' {loop.Waypoints.Count} rooms from {sender}, saved in {Location(loop)}]"
            : $"[Received loop '{original}' {loop.Waypoints.Count} rooms from {sender}, saved in {Location(loop)} as '{loop.Name}']");
    }

    // Where the loop sits in the Navigation Loops list: the root, or its folder.
    private static string Location(Loop loop) =>
        loop.Folder.Length == 0 ? "Loops" : $"Loops/{loop.Folder}";

    // The sender's name if it's free, else "<name> (from <sender>)", numbered on.
    private string FreeName(string name, string sender)
    {
        if (_loops.Get(name) is null) return name;
        string baseName = $"{name} (from {sender})";
        string candidate = baseName;
        for (int n = 2; _loops.Get(candidate) is not null; n++) candidate = $"{baseName} {n}";
        return candidate;
    }

    private static bool SameRoute(Loop a, Loop b) =>
        a.OnlyAttackInLairRooms == b.OnlyAttackInLairRooms
        && a.Waypoints.Count == b.Waypoints.Count
        && a.Waypoints.Zip(b.Waypoints).All(p =>
            p.First.Room == p.Second.Room
            && (p.First.Command ?? "") == (p.Second.Command ?? "")
            && p.First.DelayMs == p.Second.DelayMs
            && p.First.DoNotRest == p.Second.DoNotRest
            && p.First.DoNotAttack == p.Second.DoNotAttack);

    // Parse "@loopdata <id> <i>/<n> <chunk>", tolerating the {} a remote reply is
    // wrapped in.
    private static bool TryParse(string message, out string id, out int index, out int count, out string chunk)
    {
        id = chunk = string.Empty;
        index = count = 0;
        string body = message.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[^1] == '}') body = body[1..^1].Trim();

        string[] parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return false;
        if (!parts[0].Equals(LoopShareHandler.DataToken, StringComparison.OrdinalIgnoreCase)) return false;
        string[] fraction = parts[2].Split('/');
        if (fraction.Length != 2
            || !int.TryParse(fraction[0], out index) || !int.TryParse(fraction[1], out count)
            || count < 1 || count > MaxChunks || index < 1 || index > count)
            return false;
        id = parts[1];
        chunk = parts[3];
        return true;
    }

    private static bool SameGiven(string a, string b) =>
        Given(a).Equals(Given(b), StringComparison.OrdinalIgnoreCase);

    private static string Given(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
