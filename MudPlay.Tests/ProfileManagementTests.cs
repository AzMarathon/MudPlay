using System.IO;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Disk-touching tests for the Profile Management primitives (CreateProfile /
// DeleteProfile / MoveProfile). Mirrors BbsRenameCascadeTests: writes under
// uniquely-tagged throwaway BBS folders in the real data root and removes them
// in Dispose. Deliberately does NOT exercise RecentProfileList.RekeyBbs — that
// path ends in SettingsService.Save(), which would overwrite the real
// global.json; its ref-rewriting is trivial and left to the smoke test.
public sealed class ProfileManagementTests : IDisposable
{
    private readonly string _bbsA;
    private readonly string _bbsB;

    public ProfileManagementTests()
    {
        string tag = Path.GetRandomFileName();
        _bbsA = "profmgr-test-a-" + tag;
        _bbsB = "profmgr-test-b-" + tag;
        new BbsProfileStore().Save(new BbsProfile { Name = _bbsA, Host = "example.org", Port = 23 });
        new BbsProfileStore().Save(new BbsProfile { Name = _bbsB, Host = "example.org", Port = 23 });
    }

    public void Dispose()
    {
        foreach (string bbs in new[] { _bbsA, _bbsB })
        {
            try
            {
                string folder = AppPaths.BbsFolder(bbs);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void CreateProfile_WritesFile_LeavesCurrentNull_ThrowsOnClashAndBlank()
    {
        var svc = new ProfileService();
        svc.CreateProfile(_bbsA, "Alice");

        Assert.True(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Alice")));
        Assert.Null(svc.Current);   // create does NOT load the new profile

        Assert.Throws<IOException>(() => svc.CreateProfile(_bbsA, "Alice"));   // clash
        Assert.Throws<System.ArgumentException>(() => svc.CreateProfile(_bbsA, "  "));
    }

    [Fact]
    public void DeleteProfile_NonCurrent_RemovesFolder_KeepsCurrent()
    {
        var svc = new ProfileService();
        svc.CreateProfile(_bbsA, "Keep");
        svc.CreateProfile(_bbsA, "Doomed");
        svc.Load(_bbsA, "Keep");

        svc.DeleteProfile(_bbsA, "Doomed");

        Assert.False(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Doomed")));
        Assert.Equal("Keep", svc.CurrentProfileName);   // loaded profile untouched
        Assert.True(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Keep")));
    }

    [Fact]
    public void DeleteProfile_Current_FallsBackToDefault_WithoutResurrecting()
    {
        var svc = new ProfileService();
        svc.CreateProfile(_bbsA, "Solo");
        svc.Load(_bbsA, "Solo");

        bool closed = false, loaded = false;
        svc.ProfileClosed += () => closed = true;
        svc.ProfileLoaded += _ => loaded = true;

        svc.DeleteProfile(_bbsA, "Solo");

        Assert.True(closed);                 // the loaded profile was closed first
        Assert.True(loaded);                 // then the default profile loaded in its place
        Assert.NotNull(svc.Current);         // session always has a live Current
        Assert.Null(svc.CurrentProfileName); // the default profile is unnamed
        Assert.False(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Solo"))); // not resurrected by an outgoing save
    }

    [Fact]
    public void MoveProfile_SameBbsRename_NonCurrent_MovesFolder_RewritesName()
    {
        var svc = new ProfileService();
        svc.CreateProfile(_bbsA, "OldName");

        svc.MoveProfile(_bbsA, "OldName", _bbsA, "NewName");

        Assert.False(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "OldName")));
        CharacterProfile moved = JsonStore.Load<CharacterProfile>(AppPaths.CharacterProfileFile(_bbsA, "NewName"))!;
        Assert.Equal("NewName", moved.Name);
    }

    [Fact]
    public void MoveProfile_DifferentBbs_Assign_MovesFolder()
    {
        var svc = new ProfileService();
        svc.CreateProfile(_bbsA, "Wanderer");

        svc.MoveProfile(_bbsA, "Wanderer", _bbsB, "Wanderer");

        Assert.False(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Wanderer")));
        Assert.True(File.Exists(AppPaths.CharacterProfileFile(_bbsB, "Wanderer")));
    }

    [Fact]
    public void MoveProfile_Current_ReHomes_UpdatesLoadedIdentity()
    {
        var svc = new ProfileService();
        svc.CreateProfile(_bbsA, "Live");
        svc.Load(_bbsA, "Live");

        svc.MoveProfile(_bbsA, "Live", _bbsB, "Live");

        Assert.Equal(_bbsB, svc.CurrentBbsName);
        Assert.Equal("Live", svc.CurrentProfileName);
        Assert.True(File.Exists(AppPaths.CharacterProfileFile(_bbsB, "Live")));
        Assert.False(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Live")));
    }

    [Fact]
    public void MoveProfile_ThrowsOnDestinationClash()
    {
        var svc = new ProfileService();
        svc.CreateProfile(_bbsA, "One");
        svc.CreateProfile(_bbsB, "One");

        Assert.Throws<IOException>(() => svc.MoveProfile(_bbsA, "One", _bbsB, "One"));
    }
}
