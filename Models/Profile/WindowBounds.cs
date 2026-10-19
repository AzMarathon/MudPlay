using MudPlay.Services;

namespace MudPlay.Models.Profile;

// Persisted size + screen position for one top-level window. Lives in
// CharacterProfile.WindowBounds keyed by a stable window id ("main",
// "backscroll", etc.).
//
// Width / Height are device-independent pixels; X / Y are the window's Position
// (physical screen pixels). WindowLayoutStore validates the position against the
// live monitor layout on restore — a spot on a now-disconnected monitor, or one
// that falls off every screen, gets re-anchored next to the main UI.
public sealed class WindowBounds
{
    [SettingRange(-32768, 32767)]
    public double X { get; set; }
    [SettingRange(-32768, 32767)]
    public double Y { get; set; }
    [SettingRange(0, 32767)]
    public double Width { get; set; }
    [SettingRange(0, 32767)]
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
