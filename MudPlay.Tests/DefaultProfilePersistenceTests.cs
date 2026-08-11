using System.IO;
using System.Text.Json;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins the "default profile lives in the Global folder" behaviour: the persisted
// default (LoadDefaultProfile) round-trips through Save, while the in-memory
// scratch draft (LoadBlank) never writes. Both tests share the process-wide
// AppPaths.DefaultProfileFile, so they live in one class (xUnit runs a class's
// tests serially) and each brackets itself with delete-before / delete-after.
public sealed class DefaultProfilePersistenceTests
{
    [Fact]
    public void LoadDefaultProfile_SaveThenReload_PersistsSettings()
    {
        DeleteDefaultProfileFile();
        try
        {
            ProfileService writer = new();
            writer.LoadDefaultProfile();          // absent file → fresh installed defaults
            writer.Current!.Settings ??= new();
            writer.Current.Settings["Probe"] =
                JsonSerializer.SerializeToElement(new { Flag = true });
            writer.Save();

            Assert.True(File.Exists(AppPaths.DefaultProfileFile),
                "Save on the default profile should have written the Global default-profile file.");

            ProfileService reader = new();
            reader.LoadDefaultProfile();          // now reads the file the writer produced
            Assert.NotNull(reader.Current!.Settings);
            Assert.True(reader.Current.Settings!.TryGetValue("Probe", out JsonElement probe));
            Assert.True(probe.GetProperty("Flag").GetBoolean());
        }
        finally { DeleteDefaultProfileFile(); }
    }

    [Fact]
    public void LoadBlank_Save_DoesNotWriteDefaultFile()
    {
        DeleteDefaultProfileFile();
        try
        {
            ProfileService scratch = new();
            scratch.LoadBlank();                  // throwaway in-memory draft
            scratch.Current!.Settings ??= new();
            scratch.Current.Settings["Probe"] =
                JsonSerializer.SerializeToElement(new { Flag = true });
            scratch.Save();                       // must stay a no-op — nothing persisted

            Assert.False(File.Exists(AppPaths.DefaultProfileFile),
                "A LoadBlank scratch draft must never persist to the Global default-profile file.");
        }
        finally { DeleteDefaultProfileFile(); }
    }

    private static void DeleteDefaultProfileFile()
    {
        if (File.Exists(AppPaths.DefaultProfileFile)) File.Delete(AppPaths.DefaultProfileFile);
    }
}
