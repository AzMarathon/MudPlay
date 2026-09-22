using System;
using System.IO;
using System.Linq;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// User emote persistence + the shareable export/import package. Each test uses its own
// temp directory so it never touches the live Emotes folder.
public sealed class EmoteStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-emotes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string TempImage(string name)
    {
        string p = Path.Combine(_dir, name);
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(p, new byte[] { 1, 2, 3, 4 });   // stand-in bytes; the store copies, doesn't decode
        return p;
    }

    [Fact]
    public void AddUnicode_PersistsAndReloads()
    {
        var store = new EmoteStore(_dir);
        Assert.True(store.AddUnicode("boom", "💥"));

        var reloaded = new EmoteStore(_dir);
        var e = Assert.Single(reloaded.Emotes);
        Assert.Equal("boom", e.Shortcode);
        Assert.Equal("💥", e.Emoji);
    }

    [Fact]
    public void AddUnicode_RejectsBadShortcode()
    {
        var store = new EmoteStore(_dir);
        Assert.False(store.AddUnicode("has space", "💥"));
        Assert.Empty(store.Emotes);
    }

    [Fact]
    public void AddImage_CopiesFileIntoStore()
    {
        var store = new EmoteStore(_dir);
        Assert.True(store.AddImage("pic", TempImage("src.png")));
        var e = Assert.Single(store.Emotes);
        Assert.Equal("pic.png", e.Image);
        Assert.True(File.Exists(Path.Combine(_dir, "pic.png")));
    }

    [Fact]
    public void Remove_DropsEntryAndImage()
    {
        var store = new EmoteStore(_dir);
        store.AddImage("pic", TempImage("src.png"));
        Assert.True(store.Remove("pic"));
        Assert.Empty(store.Emotes);
        Assert.False(File.Exists(Path.Combine(_dir, "pic.png")));
    }

    [Fact]
    public void ExportImport_RoundTripsAcrossStores()
    {
        var src = new EmoteStore(_dir);
        src.AddUnicode("boom", "💥");
        src.AddImage("pic", TempImage("src.png"));

        string pkg = Path.Combine(_dir, "pack.mudemotes");
        Assert.True(src.Export(pkg));

        string dir2 = _dir + "-b";
        try
        {
            var dst = new EmoteStore(dir2);
            Assert.Equal(2, dst.Import(pkg));
            Assert.Contains(dst.Emotes, e => e.Shortcode == "boom" && e.Emoji == "💥");
            Assert.Contains(dst.Emotes, e => e.Shortcode == "pic" && e.Image == "pic.png");
            Assert.True(File.Exists(Path.Combine(dir2, "pic.png")));
        }
        finally { try { Directory.Delete(dir2, true); } catch { } }
    }

    [Fact]
    public void Import_BadFile_ReturnsMinusOne()
    {
        var store = new EmoteStore(_dir);
        string bad = Path.Combine(_dir, "notapack.txt");
        File.WriteAllText(bad, "nope");
        Assert.Equal(-1, store.Import(bad));
    }
}
