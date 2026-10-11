using System;
using System.IO;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// For tests that delete or move folders through the app's own stores: those work on
// AppPaths.DataRoot, which without MUDPLAY_DATA_ROOT is the user's real data.
internal static class ScratchRoot
{
    // The tests that feed the stores names they refuse run one after another: a
    // refusal is logged once and then remembered for a while, and a test that
    // counts its own would be thrown by another's arriving in between.
    public const string NamesCollection = "Names as paths";

    // Fails the calling test before it touches anything unless the data root is the
    // scratch folder the run was started with.
    public static void Require()
    {
        string? asked = Environment.GetEnvironmentVariable("MUDPLAY_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(asked),
            "This test deletes folders under the data root and will not run against a real one: set MUDPLAY_DATA_ROOT to a scratch folder.");
        Assert.Equal(DataRootSource.EnvironmentVariable, AppPaths.DataRootResolvedFrom);
        string root = Path.GetFullPath(AppPaths.DataRoot);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(asked!)), Path.TrimEndingDirectorySeparator(root));

        // Not the platform's own data folder, nor a folder inside or around it.
        string installed = Path.TrimEndingDirectorySeparator(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MudPlay"));
        string scratch = Path.TrimEndingDirectorySeparator(root);
        Assert.False(scratch.StartsWith(installed, StringComparison.OrdinalIgnoreCase)
                     || installed.StartsWith(scratch, StringComparison.OrdinalIgnoreCase),
            $"MUDPLAY_DATA_ROOT ({root}) is, holds or lies inside the installed data folder ({installed}).");
    }

    // True when path lies strictly inside the scratch data root.
    public static bool Holds(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataRoot)) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.Length > root.Length && full.StartsWith(root, StringComparison.Ordinal);
    }
}
