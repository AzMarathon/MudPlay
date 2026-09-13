using System.Text.Json;
using System.Text.Json.Serialization;

namespace MudPlay.Services.Api;

// One serializer configuration for every API response, so field naming can't
// drift between endpoints.
//
// Enums go out as names, not numbers: a consumer reading `"confidence":"Pending"`
// needs no copy of our enum ordering, and reordering an enum here can't silently
// change what a response means.
public static class LocalApiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        // Indented because these responses are read by humans and by models far
        // more often than by programs, and the payloads are small.
        WriteIndented = true,
    };

    // Same shape, single line. REQUIRED for Server-Sent Events: a frame is
    // `data: <payload>\n`, so an indented payload spills onto lines the protocol
    // reads as separate fields — a conforming client would see only the opening
    // brace. Never use the indented options for an event frame.
    public static readonly JsonSerializerOptions Compact = new(Options)
    {
        WriteIndented = false,
    };
}
