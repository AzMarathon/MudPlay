using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MudPlay.Services.Api;

// Access control for the local control API. Two independent checks, both of
// which must pass: the peer is on loopback, and it presented the bearer token.
//
// Loopback alone is NOT sufficient, which is why the token exists. Any process
// on the machine can reach 127.0.0.1, and so can a web page the user happens to
// be visiting — a page can't READ a cross-origin response, but it can certainly
// fire the request, and these endpoints act on the character. Requiring an
// Authorization header is what shuts that door: a cross-origin request carrying
// one stops being a CORS "simple request", so the browser must preflight, and we
// answer no preflight and send no CORS headers. A token in a query string or
// cookie would not have that property (and a query string leaks into logs), so
// the header is the only accepted carrier.
public sealed class LocalApiAuth
{
    // Bytes of entropy in a generated token. 32 bytes is well past the point
    // where guessing is the weak link.
    private const int TokenBytes = 32;

    private readonly string _tokenFile;
    private readonly LogService? _log;
    private string? _token;

    public LocalApiAuth(string tokenFile, LogService? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenFile);
        _tokenFile = tokenFile;
        _log = log;
    }

    // Wall-clock of the last rejected request, for the bug report. Null until one
    // happens — a populated value in a capture is worth noticing.
    public DateTimeOffset? LastAuthFailureUtc { get; private set; }

    // The live token, loading or minting it on first use. Never logged.
    public string Token => _token ??= LoadOrCreate();

    // Reason a request was refused, or null when it's allowed. The caller maps
    // this to a status code; the string is for the log, never the response body —
    // an unauthenticated caller learns only that it failed.
    public string? Reject(IPEndPoint? remote, string? authorizationHeader)
    {
        if (!IsLoopback(remote))
        {
            NoteFailure();
            return $"non-loopback peer {remote?.Address.ToString() ?? "(unknown)"}";
        }
        if (!TryReadBearer(authorizationHeader, out string presented))
        {
            NoteFailure();
            return "missing or malformed Authorization: Bearer header";
        }
        if (!FixedTimeEquals(presented, Token))
        {
            NoteFailure();
            return "token mismatch";
        }
        return null;
    }

    // The listener is bound to IPAddress.Loopback, so a non-loopback peer should
    // be impossible. Checked anyway: this is the one control standing between a
    // misconfiguration and remote control of someone's character, and it costs a
    // comparison.
    public static bool IsLoopback(IPEndPoint? remote)
        => remote?.Address is { } a && IPAddress.IsLoopback(a);

    // Accepts `Bearer <token>` with a case-insensitive scheme, as RFC 7235
    // requires. Anything else — no header, a different scheme, an empty token —
    // fails rather than being coerced.
    public static bool TryReadBearer(string? header, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(header)) return false;

        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;

        string rest = header[scheme.Length..].Trim();
        if (rest.Length == 0) return false;
        token = rest;
        return true;
    }

    // Length-independent comparison so a wrong guess reveals nothing through
    // timing. CryptographicOperations.FixedTimeEquals requires equal lengths, so
    // hash both sides first — that also stops the length itself leaking.
    public static bool FixedTimeEquals(string a, string b)
    {
        byte[] ha = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        byte[] hb = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }

    // Regenerate and persist. Exposed so the user can revoke a token that leaked
    // (or that they pasted somewhere they shouldn't have).
    public string Regenerate()
    {
        _token = Create();
        Persist(_token);
        _log?.Info(LocalApiServer.LogCategory, "API token regenerated.");
        return _token;
    }

    private void NoteFailure() => LastAuthFailureUtc = DateTimeOffset.UtcNow;

    private string LoadOrCreate()
    {
        try
        {
            if (File.Exists(_tokenFile))
            {
                string existing = File.ReadAllText(_tokenFile).Trim();
                // A truncated or hand-edited file would otherwise wedge the API
                // behind a token nobody holds. Mint a fresh one instead.
                if (existing.Length >= 32) return existing;
                _log?.Warn(LocalApiServer.LogCategory,
                    "API token file was unreadable or too short — generating a new token.");
            }
        }
        catch (Exception ex)
        {
            _log?.Warn(LocalApiServer.LogCategory,
                $"couldn't read the API token file ({ex.Message}) — generating a new token.");
        }

        string token = Create();
        Persist(token);
        return token;
    }

    private static string Create()
    {
        // base64url so the token can be pasted into a header, a shell, or a URL
        // without escaping.
        Span<byte> raw = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private void Persist(string token)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_tokenFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_tokenFile, token);
            RestrictToOwner(_tokenFile);
        }
        catch (Exception ex)
        {
            // Non-fatal: the in-memory token still works for this session, it just
            // won't survive a restart. Surfaced because a token that silently
            // changes every launch is confusing.
            _log?.Warn(LocalApiServer.LogCategory,
                $"couldn't save the API token ({ex.Message}) — it will change on restart.");
        }
    }

    // Owner read/write only. The file sits in the user's own app-data directory,
    // but this is a credential, so match how .credkey is treated rather than
    // relying on the directory's permissions.
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;   // POSIX-only API; NTFS ACLs already inherit user-scoped
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
