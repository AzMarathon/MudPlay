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

    // The startup-animation preference is install-global: written to the Global
    // default profile and read back from it regardless of which profile is loaded.
    // A reader with no profile loaded takes the SAME ReadDefaultProfileFile() branch
    // an auto-loaded NAMED profile would, so this pins the "always the Global default,
    // never the loaded character" contract that the fix hinges on.
    [Fact]
    public void StartupAnimation_WriteThenRead_RoundTripsThroughGlobalDefaultFile()
    {
        DeleteDefaultProfileFile();
        try
        {
            ProfileService writer = new();
            writer.LoadDefaultProfile();                       // default is Current
            writer.WriteDefaultProfileStartupAnimation(false); // edits Current + Saves the Global file
            Assert.False(writer.ReadDefaultProfileStartupAnimation()); // Current branch

            // A separate service with NO profile loaded reads the Global file — the
            // same branch a loaded named profile would take.
            ProfileService reader = new();
            Assert.False(reader.ReadDefaultProfileStartupAnimation());
        }
        finally { DeleteDefaultProfileFile(); }
    }

    [Fact]
    public void StartupAnimation_DefaultsToOn_WhenNoGlobalDefaultFile()
    {
        DeleteDefaultProfileFile();
        try
        {
            ProfileService svc = new();
            // Absent default file → installed default (animation on).
            Assert.True(svc.ReadDefaultProfileStartupAnimation());
        }
        finally { DeleteDefaultProfileFile(); }
    }

    private static void DeleteDefaultProfileFile()
    {
        if (File.Exists(AppPaths.DefaultProfileFile)) File.Delete(AppPaths.DefaultProfileFile);
    }
}
