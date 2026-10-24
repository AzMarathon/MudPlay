using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;

namespace MudPlay.Controls;

// A clickable run of text inside a TextBlock: hot-text drawn as part of the line,
// with the click, hand cursor and tooltip handled by the host TextBlock
// hit-testing its own text layout.
//
// Why not a Button in an InlineUIContainer: a TextBlock hidden with
// IsVisible="False" is marked measured by layout without its MeasureOverride
// ever running, so its embedded controls are never measured. It is still
// rendered when dirty, and the text layout it builds while rendering then
// measures them for the first time: a Button builds its template inside the
// render pass and Avalonia throws "Visual was invalidated during the render
// pass". Any embedded control fails the same way, since its new size
// invalidates the host. A Run is plain text, so nothing is measured or attached
// while the text draws.
public sealed class LinkRun : Run
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    // The link a left button went down on; it fires only if the button comes up
    // over the same link, as a Button's click does.
    private static LinkRun? s_pressed;
    private static TextBlock? s_pressedHost;

    private readonly Action _activate;

    // The tooltip shown while the pointer is over this link, or null for none.
    private readonly string? _tip;

    // Styled as a Run, so XAML gives a link its look with a Run.<class> selector.
    protected override Type StyleKeyOverride => typeof(Run);

    // Spaces become no-break spaces so a link wraps as one word.
    public LinkRun(string text, Action activate, string? tip = null)
        : base(text.Replace(' ', ' '))
    {
        _activate = activate;
        _tip = tip;
    }

    // A TextBlock that carries links has its cursor and tooltip driven by them;
    // every other TextBlock is left alone.
    static LinkRun()
    {
        InputElement.PointerMovedEvent.AddClassHandler<TextBlock>(OnPointerMoved);
        InputElement.PointerExitedEvent.AddClassHandler<TextBlock>((host, _) =>
        {
            if (HasLinks(host)) ShowHover(host, null);
        });
        InputElement.PointerPressedEvent.AddClassHandler<TextBlock>(OnPointerPressed);
        InputElement.PointerReleasedEvent.AddClassHandler<TextBlock>(OnPointerReleased);
        InputElement.PointerCaptureLostEvent.AddClassHandler<TextBlock>((host, _) =>
        {
            if (host == s_pressedHost) ClearPress();
        });
    }

    private static void OnPointerMoved(TextBlock host, PointerEventArgs e)
    {
        if (HasLinks(host)) ShowHover(host, LinkAt(host, e.GetPosition(host)));
    }

    private static void ShowHover(TextBlock host, LinkRun? link)
    {
        if (link is null)
        {
            host.ClearValue(InputElement.CursorProperty);
            host.ClearValue(ToolTip.TipProperty);
            return;
        }
        host.Cursor = HandCursor;
        if (link._tip is null) host.ClearValue(ToolTip.TipProperty);
        else ToolTip.SetTip(host, link._tip);
    }

    // Any left press starts over, so a press whose release never arrived can't
    // fire later. A press on a link is handled, so it never reaches a CheckBox or
    // list row around the text; a press on the prose bubbles on untouched.
    private static void OnPointerPressed(TextBlock host, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(host).Properties.IsLeftButtonPressed) return;
        ClearPress();
        if (LinkAt(host, e.GetPosition(host)) is not { } link) return;
        s_pressed = link;
        s_pressedHost = host;
        e.Pointer.Capture(host);
        e.Handled = true;
    }

    private static void OnPointerReleased(TextBlock host, PointerReleasedEventArgs e)
    {
        if (host != s_pressedHost || e.InitialPressMouseButton != MouseButton.Left) return;
        LinkRun? pressed = s_pressed;
        ClearPress();
        e.Pointer.Capture(null);
        e.Handled = true;
        if (pressed is not null && LinkAt(host, e.GetPosition(host)) == pressed)
            pressed._activate();
    }

    // Also lets go of the host, so a closed window's tree isn't kept alive.
    private static void ClearPress()
    {
        s_pressed = null;
        s_pressedHost = null;
    }

    private static bool HasLinks(TextBlock host)
    {
        if (host.Inlines is not { Count: > 0 } inlines) return false;
        foreach (Inline inline in inlines)
            if (inline is LinkRun) return true;
        return false;
    }

    // The link whose glyphs are under the point, or null. Text-layout positions
    // count each Run's characters and one slot per embedded control, the only
    // inline kinds a link host is built from.
    private static LinkRun? LinkAt(TextBlock host, Point point)
    {
        if (host.Inlines is not { Count: > 0 } inlines) return null;
        var origin = new Vector(host.Padding.Left, host.Padding.Top);
        int start = 0;
        foreach (Inline inline in inlines)
        {
            int length;
            if (inline is Run run) length = run.Text?.Length ?? 0;
            else if (inline is InlineUIContainer) length = 1;
            else return null;

            if (inline is LinkRun link)
                foreach (Rect box in host.TextLayout.HitTestTextRange(start, length))
                    if (box.Translate(origin).Contains(point)) return link;
            start += length;
        }
        return null;
    }
}
