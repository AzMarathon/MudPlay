using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A character open in one client can have its realm reassigned from another
// client's Profile Management, which rewrites the file on disk. The open client's
// next save used to write its stale in-memory realm back over it, so the character
// played (and filed its realm data) on the BBS's first realm.
public sealed class ProfileRealmSyncTests : IDisposable
{
    private readonly string _bbs = "realm-sync-test-" + Path.GetRandomFileName();

    public void Dispose()
    {
        try { if (Directory.Exists(AppPaths.BbsFolder(_bbs))) Directory.Delete(AppPaths.BbsFolder(_bbs), recursive: true); }
        catch { /* best-effort */ }
    }

    private string? RealmOnDisk(string name) =>
        JsonStore.Load<CharacterProfile>(AppPaths.CharacterProfileFile(_bbs, name))!.Realm;

    [Fact]
    public void Save_AdoptsARealmAnotherClientAssigned()
    {
        new ProfileService().CreateProfile(_bbs, "Tester");
        ProfileService open = new();
        open.Load(_bbs, "Tester");
        int repins = 0;
        open.BbsPinApplied += _ => repins++;

        new ProfileService().AssignRealm(new ProfileRef(_bbs, "Tester"), "PVP");   // the other client
        open.Save();

        Assert.Equal("PVP", open.Current!.Realm);
        Assert.Equal("PVP", RealmOnDisk("Tester"));
        Assert.Equal(1, repins);

        open.Save();   // nothing new on disk: no second re-pin
        Assert.Equal(1, repins);
    }

    [Fact]
    public void Save_KeepsOurOwnRealmChange()
    {
        new ProfileService().CreateProfile(_bbs, "Tester");
        ProfileService open = new();
        open.Load(_bbs, "Tester");

        new ProfileService().AssignRealm(new ProfileRef(_bbs, "Tester"), "PVP");
        open.AssignRealm(new ProfileRef(_bbs, "Tester"), "PVE");   // this client's own pick, made after

        Assert.Equal("PVE", open.Current!.Realm);
        Assert.Equal("PVE", RealmOnDisk("Tester"));
    }
}
