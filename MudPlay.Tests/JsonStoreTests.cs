using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// JsonStore.Save is the shared atomic-write helper. Its regression concern is the
// crash that took down two client instances sharing a realm-wide file
// (boss-timers.json): both wrote the same fixed "…json.tmp" temp and the second
// File.Move threw FileNotFoundException once the first had renamed it away. The
// fix gives each write a unique temp name, so racing writers to one target never
// collide — the last rename simply wins.
public sealed class JsonStoreTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "jsonstore-" + Path.GetRandomFileName() + ".json");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best-effort */ }
    }

    private sealed class Payload
    {
        public int Value { get; set; }
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        JsonStore.Save(_path, new Payload { Value = 42 });
        Assert.Equal(42, JsonStore.Load<Payload>(_path)!.Value);
    }

    [Fact]
    public void Save_LeavesNoTempFilesBehind()
    {
        JsonStore.Save(_path, new Payload { Value = 7 });
        string? dir = Path.GetDirectoryName(_path);
        Assert.Empty(Directory.EnumerateFiles(dir!, Path.GetFileName(_path) + ".*.tmp"));
    }

    [Fact]
    public async Task ConcurrentSaves_ToSameTarget_DoNotThrow()
    {
        // Stand-in for two client instances persisting the same realm-wide file at
        // once: many writers racing on one target. The old fixed-temp write threw
        // FileNotFoundException here when one File.Move renamed the shared temp out
        // from under another; the unique-temp write must let them all through and
        // leave a valid file (last writer wins).
        var writers = Enumerable.Range(0, 64).Select(i => Task.Run(() =>
            JsonStore.Save(_path, new Payload { Value = i })));

        Exception? failure = await Record.ExceptionAsync(() => Task.WhenAll(writers));

        Assert.Null(failure);
        Assert.NotNull(JsonStore.Load<Payload>(_path));   // parseable, not half-written
        string? dir = Path.GetDirectoryName(_path);
        Assert.Empty(Directory.EnumerateFiles(dir!, Path.GetFileName(_path) + ".*.tmp"));
    }
}
