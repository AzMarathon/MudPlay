using System.Text.Json;

namespace MudPlay.Net;

public readonly record struct GmcpMessage(string Package, JsonElement Payload);
