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

        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MudPlay");
        Assert.Null(WhyNotScratch(root, installed));
    }

    // Why root can't stand in for the app's data folder installed, or null when it
    // can: not the data folder, nor a folder inside or around it, by where each really
    // lies once every symbolic link in either path is followed.
    internal static string? WhyNotScratch(string root, string installed)
    {
        string scratch = Resolve(root), live = Resolve(installed);
        return scratch.StartsWith(live, StringComparison.OrdinalIgnoreCase)
               || live.StartsWith(scratch, StringComparison.OrdinalIgnoreCase)
            ? $"MUDPLAY_DATA_ROOT ({root}, which is {scratch}) is, holds or lies inside the installed data folder ({installed}, which is {live})."
            : null;
    }

    // path as a full path with the symbolic links in it, in the folder itself and in
    // each ancestor that exists, followed to what they point at.
    internal static string Resolve(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetDirectoryName(full) is not { } parent) return full;
        string at = Path.Combine(Resolve(parent), Path.GetFileName(full));
        DirectoryInfo info = new(at);
        return info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
            ? Resolve(target.FullName)
            : at;
    }

    // True when path lies strictly inside the scratch data root.
    public static bool Holds(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataRoot)) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.Length > root.Length && full.StartsWith(root, StringComparison.Ordinal);
    }
}
