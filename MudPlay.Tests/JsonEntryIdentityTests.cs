using System.Text.Json;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class JsonEntryIdentityTests
{
    private sealed record Sample(int A, string B);

    [Fact]
    public void An_entry_read_back_from_the_dictionary_is_the_same_entry()
    {
        Dictionary<string, JsonElement> settings = new() { ["General"] = JsonSerializer.SerializeToElement(new Sample(1, "x")) };
        JsonElement first = settings["General"];
        JsonElement second = settings["General"];

        Assert.True(JsonEntryIdentity.Same(first, second));
    }

    [Fact]
    public void A_rewritten_entry_is_a_different_entry_even_with_equal_content()
    {
        JsonElement before = JsonSerializer.SerializeToElement(new Sample(1, "x"));
        JsonElement after = JsonSerializer.SerializeToElement(new Sample(1, "x"));

        Assert.False(JsonEntryIdentity.Same(before, after));
    }

    [Fact]
    public void A_missing_entry_matches_only_a_missing_entry()
    {
        JsonElement present = JsonSerializer.SerializeToElement(new Sample(1, "x"));

        Assert.True(JsonEntryIdentity.Same(default, default));
        Assert.False(JsonEntryIdentity.Same(default, present));
        Assert.False(JsonEntryIdentity.Same(present, default));
    }

    [Fact]
    public void Two_properties_of_one_document_are_different_entries()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"a":{"n":1},"b":{"n":1}}""");

        Assert.False(JsonEntryIdentity.Same(doc.RootElement.GetProperty("a"), doc.RootElement.GetProperty("b")));
    }
}
