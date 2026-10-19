using Xunit;

namespace MudPlay.Tests;

// Every SettingsService reads the one global.json under the data root. A test that
// writes it runs on its own, so no other test's SettingsService reads its values.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalSettingsFileCollection
{
    public const string Name = "Global settings file";
}
