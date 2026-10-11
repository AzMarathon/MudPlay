namespace MudPlay.Services;

public sealed partial class GameDataCache
{
    // A table file as the cache found it when it read it: length and last-write
    // time, taken from the open file of that read. GameDataCache.FileStampAsRead
    // hands it out; two of them being equal is how a saved result tells that it
    // was worked out from the same file as the one loaded now.
    internal readonly record struct TableFileStamp(long Length, DateTime LastWriteUtc);
}
