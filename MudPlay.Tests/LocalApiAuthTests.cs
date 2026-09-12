using System.IO;
using System.Net;
using MudPlay.Services.Api;
using Xunit;

namespace MudPlay.Tests;

// The local API's access control. Loopback and the bearer token are independent
// gates and both must hold — loopback alone isn't enough, because any local
// process (or a page the user is visiting) can reach 127.0.0.1, and these
// endpoints act on the character.
public sealed class LocalApiAuthTests : IDisposable
{
    private readonly string _dir;

    public LocalApiAuthTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-apiauth-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private LocalApiAuth NewAuth(string file = "token") =>
        new(Path.Combine(_dir, file));

    private static IPEndPoint Loopback => new(IPAddress.Loopback, 5555);

    [Fact]
    public void CorrectToken_FromLoopback_Accepted()
    {
        LocalApiAuth auth = NewAuth();
        Assert.Null(auth.Reject(Loopback, $"Bearer {auth.Token}"));
        Assert.Null(auth.LastAuthFailureUtc);
    }

    [Fact]
    public void WrongToken_Rejected()
    {
        LocalApiAuth auth = NewAuth();
        Assert.NotNull(auth.Reject(Loopback, "Bearer not-the-token-not-the-token-xx"));
        Assert.NotNull(auth.LastAuthFailureUtc);   // recorded for the bug report
    }

    [Fact]
    public void MissingHeader_Rejected()
    {
        LocalApiAuth auth = NewAuth();
        Assert.NotNull(auth.Reject(Loopback, null));
        Assert.NotNull(auth.Reject(Loopback, ""));
    }

    [Fact]
    public void WrongScheme_Rejected()
    {
        // Basic-with-the-right-secret must fail: accepting any scheme would let a
        // browser send the credential as a "simple" cross-origin request.
        LocalApiAuth auth = NewAuth();
        Assert.NotNull(auth.Reject(Loopback, $"Basic {auth.Token}"));
    }

    [Fact]
    public void NonLoopbackPeer_RejectedEvenWithTheRightToken()
    {
        // The listener binds loopback so this shouldn't be reachable — checked
        // anyway, because it's the one control between a misconfiguration and
        // someone else driving the character.
        LocalApiAuth auth = NewAuth();
        IPEndPoint remote = new(IPAddress.Parse("192.168.1.50"), 5555);
        Assert.NotNull(auth.Reject(remote, $"Bearer {auth.Token}"));
    }

    [Fact]
    public void BearerScheme_IsCaseInsensitive_AndTrims()
    {
        // RFC 7235 says the scheme is case-insensitive; a client sending "bearer"
        // is correct and must not be refused.
        LocalApiAuth auth = NewAuth();
        Assert.Null(auth.Reject(Loopback, $"bearer   {auth.Token}  "));
    }

    [Fact]
    public void Token_PersistsAcrossInstances()
    {
        LocalApiAuth first = NewAuth("shared");
        string token = first.Token;
        Assert.Equal(token, NewAuth("shared").Token);
    }

    [Fact]
    public void Token_RegeneratedWhenFileIsTruncated()
    {
        // A hand-edited or partially-written file would otherwise wedge the API
        // behind a token nobody holds.
        string path = Path.Combine(_dir, "short");
        File.WriteAllText(path, "abc");
        LocalApiAuth auth = new(path);
        Assert.True(auth.Token.Length >= 32);
        Assert.NotEqual("abc", auth.Token);
    }

    [Fact]
    public void Regenerate_InvalidatesThePreviousToken()
    {
        LocalApiAuth auth = NewAuth();
        string old = auth.Token;
        string fresh = auth.Regenerate();

        Assert.NotEqual(old, fresh);
        Assert.NotNull(auth.Reject(Loopback, $"Bearer {old}"));
        Assert.Null(auth.Reject(Loopback, $"Bearer {fresh}"));
    }

    [Fact]
    public void TryReadBearer_RejectsAnEmptyCredential()
    {
        Assert.False(LocalApiAuth.TryReadBearer("Bearer ", out _));
        Assert.False(LocalApiAuth.TryReadBearer("Bearer", out _));
        Assert.True(LocalApiAuth.TryReadBearer("Bearer abc", out string t));
        Assert.Equal("abc", t);
    }

    [Fact]
    public void FixedTimeEquals_ComparesByValue_IncludingDifferentLengths()
    {
        Assert.True(LocalApiAuth.FixedTimeEquals("same", "same"));
        Assert.False(LocalApiAuth.FixedTimeEquals("short", "considerably-longer"));
    }
}
