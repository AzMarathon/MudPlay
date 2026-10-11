using System.Text;
using MudPlay.Services;

namespace MudPlay.Tests;

// Game-data tables and rows from inline JSON, read by the reader the cache itself
// uses, for tests that hand a row straight to the code under test.
internal static class TestRows
{
    public static GameTable Table(string json)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        return GameTableReader.Read(stream);
    }

    // The row a single JSON object makes.
    public static GameRow Row(string json) => Table("[" + json + "]").Rows[0];
}
