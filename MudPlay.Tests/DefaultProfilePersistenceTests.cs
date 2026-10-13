using System.IO;
using System.Text.Json;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins what the default profile is: the template in the Global folder that a new
// character starts from, and never something a session writes. A session on it
// (LoadDefaultProfile) or on a scratch draft (LoadBlank) is an unnamed draft whose
// Save writes nothing; naming it is what keeps it. The tests share the process-wide
// AppPaths.DefaultProfileFile, so they live in one class (xUnit runs a class's
// tests serially) and each brackets itself with delete-before / delete-after.
public sealed class DefaultProfilePersistenceTests
{
    // A player who never named a character used to have every save go into the
    // template, so their setup became what each new character started from and
    // they had no profile of their own (user, 2026-10-12). The template is not
    // written from a session any more; that a save was wanted is remembered, so
    // the client can ask before the draft is lost.
    [Fact]
    public void LoadDefaultProfile_Save_WritesNothing_AndRemembersThereIsSomethingToKeep()
    {
        string board = "defprof-test-" + Path.GetRandomFileName();
        DeleteDefaultProfileFile();
        try
        {
            ProfileService session = new();
            session.LoadDefaultProfile();
            Assert.False(session.UnnamedDraftHasChanges);       // nothing done yet

            session.Current!.Settings ??= new();
            session.Current.Settings["Probe"] = JsonSerializer.SerializeToElement(new { Flag = true });
            session.Save();

            Assert.False(File.Exists(AppPaths.DefaultProfileFile));
            Assert.True(session.UnnamedDraftHasChanges);

            // Named, it is a character holding what was set up, and nothing is owed.
            session.SaveAs(board, "Named");
            Assert.False(session.UnnamedDraftHasChanges);
            Assert.True(JsonStore.Load<Models.Profile.CharacterProfile>(
                AppPaths.CharacterProfileFile(board, "Named"))!.Settings!.ContainsKey("Probe"));

            // The next draft starts from the template as it was, not from that session.
            ProfileService next = new();
            next.LoadDefaultProfile();
            Assert.False(next.Current!.Settings?.ContainsKey("Probe") == true);
        }
        finally
        {
            DeleteDefaultProfileFile();
            if (Directory.Exists(AppPaths.BbsFolder(board))) Directory.Delete(AppPaths.BbsFolder(board), recursive: true);
        }
    }

    // Discarding is the user saying the draft need not be kept: nothing asks again.
    [Fact]
    public void DiscardingTheDraftsChanges_LeavesNothingToAskAbout()
    {
        ProfileService session = new();
        session.LoadDefaultProfile();
        session.Save();
        Assert.True(session.UnnamedDraftHasChanges);

        session.DiscardUnnamedDraftChanges();

        Assert.False(session.UnnamedDraftHasChanges);
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
            writer.WriteDefaultProfileStartupAnimation(false); // edits Current, and writes the Global file itself
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

    // The default profile is the template a new character is made from. A list of
    // what some character held, left on it, must not be handed to the new one.
    [Fact]
    public void CreateProfile_FromATemplateCarryingAHeldList_StartsWithNone()
    {
        string board = "defprof-test-" + Path.GetRandomFileName();
        DeleteDefaultProfileFile();
        try
        {
            // A template from when a session on the default profile was saved into it.
            JsonStore.Save(AppPaths.DefaultProfileFile, new Models.Profile.CharacterProfile
            {
                HeldAtDisconnect = new Models.Profile.HeldAtDisconnect
                {
                    Items = { new Models.Profile.HeldItem("silver chalice", 1) },
                },
            });

            new ProfileService().CreateProfile(board, "Newcomer");

            Assert.Null(JsonStore.Load<Models.Profile.CharacterProfile>(
                AppPaths.CharacterProfileFile(board, "Newcomer"))!.HeldAtDisconnect);
        }
        finally
        {
            DeleteDefaultProfileFile();
            if (Directory.Exists(AppPaths.BbsFolder(board))) Directory.Delete(AppPaths.BbsFolder(board), recursive: true);
        }
    }

    // New profiles started with no GOTO favourites for a while, and a session on the
    // default profile saved that empty list into the template. The template can't be
    // changed now, so an empty list on it reads as "not taken": the draft, and a
    // character made from it, take the starters. A list with something in it is kept.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemplateWithAnEmptyFavouritesList_LeavesTheListForTheStartersToFill(bool templateHasFavourites)
    {
        string board = "defprof-test-" + Path.GetRandomFileName();
        DeleteDefaultProfileFile();
        try
        {
            JsonStore.Save(AppPaths.DefaultProfileFile, new Models.Profile.CharacterProfile
            {
                Favorites = templateHasFavourites
                    ? new() { new Models.Profile.FavoriteRoom { Map = 1, Room = 45, Label = "Bank" } }
                    : new(),
                FavoriteFolders = new(),
            });

            ProfileService draft = new();
            draft.LoadDefaultProfile();
            new ProfileService().CreateProfile(board, "Newcomer");
            Models.Profile.CharacterProfile made = JsonStore.Load<Models.Profile.CharacterProfile>(
                AppPaths.CharacterProfileFile(board, "Newcomer"))!;

            if (templateHasFavourites)
            {
                Assert.Single(draft.Current!.Favorites!);
                Assert.Single(made.Favorites!);
            }
            else
            {
                Assert.Null(draft.Current!.Favorites);
                Assert.Null(made.Favorites);
            }
        }
        finally
        {
            DeleteDefaultProfileFile();
            if (Directory.Exists(AppPaths.BbsFolder(board))) Directory.Delete(AppPaths.BbsFolder(board), recursive: true);
        }
    }

    private static void DeleteDefaultProfileFile()
    {
        if (File.Exists(AppPaths.DefaultProfileFile)) File.Delete(AppPaths.DefaultProfileFile);
    }
}
