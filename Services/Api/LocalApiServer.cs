using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using MudPlay.Game.Map;
using MudPlay.Terminal;

namespace MudPlay.Services.Api;

// Loopback-only HTTP control API for the running client. Off unless
// Settings.Global.LocalApiEnabled is set — it opens a listening socket, so it is
// opt-in rather than something a user discovers they've been running.
//
// Why this exists: diagnosing a wedged client meant waiting for it to break,
// capturing a bug report, and reconstructing the cause from a 750-entry log tail
// that had usually already scrolled past the failure. The state that would have
// answered the question was in memory the whole time and simply unreachable.
//
// THREADING. HttpListener hands each request to a thread-pool thread. Nearly all
// game state is UI-thread-confined (RoomTracker, the engines, the view-models),
// so every handler that touches it goes through Dispatcher.UIThread.InvokeAsync
// and awaits. The accept loop itself never touches game state, and handlers must
// stay short — this borrows the render thread.
//
// HttpListener on Windows is backed by http.sys, where a non-admin process needs
// a `netsh http add urlacl` reservation to bind. On macOS/Linux (this project's
// primary targets) it's a managed socket listener with no such requirement. The
// bind failure is caught and surfaced rather than crashing; see Start.
public sealed class LocalApiServer : IAsyncDisposable
{
    public const string LogCategory = "LocalApi";

    // MMUD on a phone keypad.
    public const int DefaultPort = 6683;

    private readonly AppServices _services;
    private readonly LogService _log;
    private readonly Func<TerminalEmulator?> _emulator;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public LocalApiServer(AppServices services, LogService log, Func<TerminalEmulator?> emulator)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(emulator);
        _services = services;
        _log = log;
        _emulator = emulator;
        Auth = new LocalApiAuth(Path.Combine(AppPaths.DataRoot, ".apitoken"), log);
        Events = new LocalApiEvents(log);
    }

    public LocalApiAuth Auth { get; }
    public LocalApiEvents Events { get; }

    public bool IsListening => _listener?.IsListening == true;
    public int Port { get; private set; }

    // Why the last Start attempt failed, for the bug report and the settings UI.
    // Null when listening or never started.
    public string? LastStartError { get; private set; }

    public void Start(int port)
    {
        if (IsListening) return;
        Port = port <= 0 || port > 65535 ? DefaultPort : port;
        LastStartError = null;

        try
        {
            // Materialise the token before accepting anything. It's lazy, and
            // Reject() short-circuits on a missing Authorization header without
            // ever reading it — so without this the file only appeared after a
            // successful request, which can't happen until the user can read the
            // file. Chicken and egg.
            _ = Auth.Token;

            HttpListener listener = new();
            // 127.0.0.1 explicitly, never '+' or '*': the prefix IS the primary
            // access control, and a wildcard would expose the character to the
            // whole network.
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            AttachEventSources();
            _log.Info(LogCategory, $"listening on http://127.0.0.1:{Port}/ (loopback only, token required).");
        }
        catch (Exception ex)
        {
            LastStartError = ex.Message;
            _listener = null;
            // A port already in use or a missing Windows urlacl must not take the
            // client down — the API is a diagnostic, not a dependency.
            _log.Error(LogCategory, $"couldn't listen on 127.0.0.1:{Port} — {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_listener is null) return;
        _cts?.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { /* shutting down */ }
        _listener = null;
        _log.Info(LogCategory, "stopped listening.");
    }

    // Apply the current settings — called on startup and whenever the user
    // changes the toggle or the port, so the socket follows the setting without
    // a restart.
    public void ApplySettings(bool enabled, int port)
    {
        if (!enabled) { Stop(); return; }
        if (IsListening && Port == port) return;
        Stop();
        Start(port);
    }

    // Subscribe the event stream to the signals worth watching live. Attached on
    // the first successful Start and left attached: LogService and the
    // coordinator outlive every listen/stop cycle, and LocalApiEvents no-ops when
    // nobody is subscribed, so there's nothing to gain by unsubscribing.
    private void AttachEventSources()
    {
        if (_eventsAttached) return;
        _eventsAttached = true;

        _log.EntryAdded += e => Events.Send("log", new
        {
            at = e.Timestamp,
            severity = e.Severity.ToString(),
            source = e.Source,
            message = e.Message,
        });

        // The signal that mattered most in every wedge so far: which gate just
        // went on or off, who did it, and why. GatesChanged carries no payload,
        // but RecordTransition appends to History before raising it, so the newest
        // history entry IS the transition being announced.
        _services.MovementCoordinator.GatesChanged += () =>
        {
            if (_services.MovementCoordinator.History is not { Count: > 0 } h) return;
            GateTransitionEntry t = h[^1];
            Events.Send("gate", new
            {
                at = t.Timestamp,
                gate = t.Gate,
                action = t.Asserted ? "assert" : "clear",
                asserter = t.Asserter,
                reason = t.Reason,
                assertedNow = _services.MovementCoordinator.AssertedGates.ToArray(),
            });
        };
    }

    private bool _eventsAttached;

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }        // Stop() closed it
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

            // Each request on its own task so one slow SSE subscriber can't stall
            // the accept loop.
            _ = Task.Run(() => HandleSafelyAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleSafelyAsync(HttpListenerContext ctx)
    {
        try { await HandleAsync(ctx).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _log.Warn(LogCategory, $"request failed: {ex.Message}");
            try { WriteJson(ctx.Response, 500, new { error = "internal error" }); } catch { /* peer gone */ }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse res = ctx.Response;
        string path = req.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        if (path.Length == 0) path = "/";

        // /health is the one open route. It answers "is MudPlay up?" and nothing
        // else — no character, no state — so it's safe to leave unauthenticated
        // and lets a caller distinguish "not running" from "wrong token".
        if (path == "/health")
        {
            WriteJson(res, 200, new
            {
                ok = true,
                version = AppInfo.Version,
                listening = true,
                authRequired = true,
            });
            return;
        }

        if (Auth.Reject(req.RemoteEndPoint, req.Headers["Authorization"]) is { } why)
        {
            // Warn, not Debug: a rejected request on a loopback-only port is
            // either a misconfigured tool or something probing, and both are
            // worth seeing in a capture.
            _log.Warn(LogCategory, $"rejected {req.HttpMethod} {path} — {why}");
            res.Headers["WWW-Authenticate"] = "Bearer";
            WriteJson(res, 401, new { error = "unauthorized" });
            return;
        }

        _log.Debug(LogCategory, $"{req.HttpMethod} {path}{req.Url?.Query}");

        if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            // PR 1 is read-only; actions land in the follow-up. Say so explicitly
            // rather than 404-ing a path that will exist shortly.
            WriteJson(res, 405, new { error = "read-only API; actions are not enabled in this build" });
            return;
        }

        switch (path)
        {
            case "/state":
                WriteJson(res, 200, await OnUiAsync(() => LocalApiState.Snapshot(_services)).ConfigureAwait(false));
                return;

            case "/state/full":
            {
                if (_emulator() is not { } emu)
                {
                    WriteJson(res, 503, new { error = "no terminal session yet" });
                    return;
                }
                bool markdown = string.Equals(req.QueryString["format"], "markdown", StringComparison.OrdinalIgnoreCase);
                if (markdown)
                {
                    string md = await OnUiAsync(() => LocalApiState.FullMarkdown(_services, emu)).ConfigureAwait(false);
                    WriteText(res, 200, md, "text/markdown; charset=utf-8");
                }
                else
                {
                    WriteJson(res, 200, await OnUiAsync(() => LocalApiState.Full(_services, emu)).ConfigureAwait(false));
                }
                return;
            }

            case "/gates":
                WriteJson(res, 200, await OnUiAsync(() => LocalApiState.Gates(_services)).ConfigureAwait(false));
                return;

            case "/log":
            {
                long since = ParseLong(req.QueryString["since"], 0);
                int limit = Math.Clamp(ParseInt(req.QueryString["limit"], 500), 1, 5000);
                IReadOnlySet<LogSeverity> severities = ParseSeverities(req.QueryString["severity"]);
                string? source = req.QueryString["source"];
                // The ring is lock-guarded and LogEntry is immutable, so this one
                // needs no dispatcher hop.
                WriteJson(res, 200, LocalApiState.Log(_services, since, limit, severities, source));
                return;
            }

            case "/scrollback":
            {
                if (_emulator() is not { } emu)
                {
                    WriteJson(res, 503, new { error = "no terminal session yet" });
                    return;
                }
                int lines = Math.Clamp(ParseInt(req.QueryString["lines"], 750), 1, 5000);
                WriteJson(res, 200, await OnUiAsync(() => LocalApiState.Scrollback(_services, emu, lines)).ConfigureAwait(false));
                return;
            }

            case "/loops":
                WriteJson(res, 200, await OnUiAsync(() => LocalApiCatalog.Loops(_services)).ConfigureAwait(false));
                return;

            case "/events":
                await StreamEventsAsync(res).ConfigureAwait(false);
                return;

            default:
                await HandleParameterisedGetAsync(res, path).ConfigureAwait(false);
                return;
        }
    }

    // Routes carrying a value in the path. Kept out of the switch above because a
    // switch can only match constants, and these need prefix matching.
    //
    // Segments are unescaped explicitly: loop names contain spaces and brackets
    // ("Storm Mountain Path (Top Third)"), so a caller must percent-encode them
    // and we must decode before looking anything up.
    private async Task HandleParameterisedGetAsync(HttpListenerResponse res, string path)
    {
        if (TrySegment(path, "/loops/", out string loopName))
        {
            object? loop = await OnUiAsync(() => LocalApiCatalog.Loop(_services, loopName)).ConfigureAwait(false);
            if (loop is null) WriteJson(res, 404, new { error = "no such loop", name = loopName });
            else WriteJson(res, 200, loop);
            return;
        }

        if (TrySegment(path, "/monsters/", out string monsterRaw))
        {
            if (!int.TryParse(monsterRaw, out int id))
            {
                WriteJson(res, 400, new { error = "monster id must be a number", got = monsterRaw });
                return;
            }
            object? mon = await OnUiAsync(() => LocalApiCatalog.Monster(_services, id)).ConfigureAwait(false);
            if (mon is null) WriteJson(res, 404, new { error = "no such monster", id });
            else WriteJson(res, 200, mon);
            return;
        }

        if (TrySegment(path, "/rooms/", out string roomRaw))
        {
            string[] parts = roomRaw.Split('/', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int map) || !int.TryParse(parts[1], out int room))
            {
                WriteJson(res, 400, new { error = "expected /rooms/{map}/{room}", got = roomRaw });
                return;
            }
            WriteJson(res, 200, await OnUiAsync(() =>
                LocalApiCatalog.Room(_services, new RoomKey(map, room))).ConfigureAwait(false));
            return;
        }

        WriteJson(res, 404, new { error = "no such endpoint", path });
    }

    private static bool TrySegment(string path, string prefix, out string rest)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = string.Empty;
            return false;
        }
        rest = Uri.UnescapeDataString(path[prefix.Length..]);
        return rest.Length > 0;
    }

    // Holds the response open until the reader disconnects. The task parked here
    // is why each request runs on its own task.
    private async Task StreamEventsAsync(HttpListenerResponse res)
    {
        Guid id = Events.Add(res);
        _log.Info(LogCategory, $"event subscriber attached ({Events.SubscriberCount} active).");
        try
        {
            // Nothing to do but wait: LocalApiEvents writes to the stream from
            // wherever events originate. The heartbeat is what detects a dead
            // reader — a failed write retires the subscription, which drops us out
            // of this loop.
            while (Events.Contains(id) && IsListening)
                await Task.Delay(1000).ConfigureAwait(false);
        }
        finally
        {
            Events.Remove(id);
            _log.Info(LogCategory, $"event subscriber detached ({Events.SubscriberCount} active).");
        }
    }

    // Marshal onto the UI thread and bring the value back. Everything reading
    // game state goes through here — see the threading note on the class.
    private static Task<T> OnUiAsync<T>(Func<T> read)
        => Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(read())
            : Dispatcher.UIThread.InvokeAsync(read).GetTask();

    private static void WriteJson(HttpListenerResponse res, int status, object payload)
        => WriteText(res, status, JsonSerializer.Serialize(payload, LocalApiJson.Options),
            "application/json; charset=utf-8");

    private static void WriteText(HttpListenerResponse res, int status, string body, string contentType)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = bytes.Length;
        // Belt and braces against a browser treating a response as a page.
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Close();
    }

    private static long ParseLong(string? raw, long fallback)
        => long.TryParse(raw, out long v) && v >= 0 ? v : fallback;

    private static int ParseInt(string? raw, int fallback)
        => int.TryParse(raw, out int v) ? v : fallback;

    // Comma-separated severity names, matched as a SET — see LocalApiState.Log
    // for why this can't be a minimum. `?severity=warn,error` gets the two that
    // matter without dragging in combat traces.
    //
    // Unrecognised names are skipped rather than rejected: a typo shouldn't turn
    // a diagnostic request into an error. An entirely unrecognised value yields
    // the empty set, i.e. no filtering — the caller sees everything rather than
    // an empty result they might read as "nothing happened".
    public static IReadOnlySet<LogSeverity> ParseSeverities(string? raw)
    {
        HashSet<LogSeverity> set = [];
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Enum.TryParse(part, ignoreCase: true, out LogSeverity s)) set.Add(s);
        return set;
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        Events.Dispose();
        if (_acceptLoop is { } loop)
        {
            try { await loop.ConfigureAwait(false); } catch { /* cancelled */ }
        }
        _cts?.Dispose();
    }
}
