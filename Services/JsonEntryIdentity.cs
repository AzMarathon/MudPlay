using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MudPlay.Services;

// Whether two JsonElements are the same stored element, not just equal JSON. A
// settings entry is only ever replaced whole, with a freshly serialized element in
// a document of its own, so "the same element" means "not written since", which is
// what lets a parsed copy of the entry be kept. JsonElement has no identity of its
// own (its struct equality works by reflection, too slow for a per-line check), but
// its raw bytes sit in its document's buffer: the same entry is the same bytes at
// the same place.
internal static class JsonEntryIdentity
{
    public static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Undefined || b.ValueKind == JsonValueKind.Undefined)
            return a.ValueKind == b.ValueKind;
        ReadOnlySpan<byte> x = JsonMarshal.GetRawUtf8Value(a);
        ReadOnlySpan<byte> y = JsonMarshal.GetRawUtf8Value(b);
        return x.Length == y.Length
            && Unsafe.AreSame(ref MemoryMarshal.GetReference(x), ref MemoryMarshal.GetReference(y));
    }
}
