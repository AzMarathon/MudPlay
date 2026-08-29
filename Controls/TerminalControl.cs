using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MudPlay.Terminal;

namespace MudPlay.Controls;

// Custom Avalonia control that draws the terminal grid and forwards keyboard
// input back out to the view-model.
//
// Rendering pipeline:
//   1. Compute per-cell pixel size from the chosen monospace font.
//   2. For each row, walk left-to-right grouping consecutive cells that
//      share the same attributes into "runs" (single fill + per-glyph draw).
//   3. After all cells are drawn, paint the cursor caret if visible.
//
// Input pipeline:
//   - OnTextInput catches normal printable text and posts the bytes
//     (Latin-1 encoded) to UserInput.
//   - OnKeyDown maps non-text keys (arrows, Enter, Ctrl+letter, F1–F4) to
//     the matching ANSI/VT escape sequences and emits those.
public sealed class TerminalControl : Control
{
    // The emulator whose screen we render. Bound from XAML.
    public static readonly StyledProperty<TerminalEmulator?> EmulatorProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalEmulator?>(nameof(Emulator));

    // Bitmap-style monospace font; defaults to embedded MX437.
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalControl, FontFamily>(
            nameof(FontFamily),
            new FontFamily("avares://MudPlay/Assets/Fonts/Mx437_IBM_VGA_8x16.ttf#Mx437 IBM VGA 8x16"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(FontSize), 16.0);

    // When true, scale the glyphs up to fill ViewportSize while keeping the
    // fixed cell grid (cols/rows unchanged — a purely visual zoom). Bound from
    // MainWindowViewModel.ScaleTerminalToWindow.
    public static readonly StyledProperty<bool> ScaleToFitProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(ScaleToFit));

    // The area the terminal should fill when ScaleToFit is on — fed from the
    // hosting ScrollViewer's bounds by MainWindow code-behind. The control lives
    // inside a ScrollViewer, so MeasureOverride can't read the window size
    // itself (it's measured with infinite available size); this property is how
    // the viewport reaches the scaling math. Zero size means "unknown" → no
    // scaling.
    public static readonly StyledProperty<Size> ViewportSizeProperty =
        AvaloniaProperty.Register<TerminalControl, Size>(nameof(ViewportSize));

    // When true, the startup mud splash plays over the terminal (drawn from a
    // standalone screen, never the emulator/scrollback). MainWindowViewModel
    // sets it true at launch and clears it on connect / first data / profile load.
    public static readonly StyledProperty<bool> SplashActiveProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(SplashActive));

    public bool SplashActive
    {
        get => GetValue(SplashActiveProperty);
        set => SetValue(SplashActiveProperty, value);
    }

    // Whether the mud figure animates. When false the splash shows only the
    // static header (title + byline + hint). Bound from the General setting.
    public static readonly StyledProperty<bool> SplashAnimateProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(SplashAnimate), defaultValue: true);

    public bool SplashAnimate
    {
        get => GetValue(SplashAnimateProperty);
        set => SetValue(SplashAnimateProperty, value);
    }
    private MudSplashAnimator? _splash;

    public TerminalEmulator? Emulator
    {
        get => GetValue(EmulatorProperty);
        set => SetValue(EmulatorProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public bool ScaleToFit
    {
        get => GetValue(ScaleToFitProperty);
        set => SetValue(ScaleToFitProperty, value);
    }

    public Size ViewportSize
    {
        get => GetValue(ViewportSizeProperty);
        set => SetValue(ViewportSizeProperty, value);
    }

    // Raised on the UI thread with bytes to send to the host.
    public event Action<byte[]>? UserInput;

    // Optional client-side line buffer. When set, printable keystrokes
    // accumulate locally and only flush to the wire on Enter — so engine
    // auto-sends (par poll, AutoParty invite, @health round-trip, etc.) can't
    // interleave into the user's half-typed input on the server side. See
    // Terminal.LocalInputBuffer for rationale. When null (no buffer attached)
    // the control falls back to the classic character-mode path — every
    // keystroke straight to the wire.
    public LocalInputBuffer? InputBuffer { get; set; }

    // Hard ceiling on the ScaleToFit zoom: the effective font never exceeds
    // FontSize × this. 2.0 left dead unpainted space on a maximised window on
    // a big/4K monitor (fitting an 80x25 grid into e.g. 3840x2160 wants ~5.4x);
    // 8.0 comfortably covers that without the nearest-neighbour upscale
    // getting visibly chunky.
    private const double MaxScale = 8.0;

    private Typeface _typeface;
    // Bold variant, cached alongside _typeface. DrawRun would otherwise allocate a
    // fresh bold Typeface — a managed wrapper over native Skia/HarfBuzz shaping
    // resources — for every bold run on every frame, churning native memory.
    private Typeface _typefaceBold;
    // Native cell box at FontSize. Glyphs are ALWAYS drawn at this size; any
    // window fitting happens by upscaling the rendered bitmap, never by
    // rasterising the bitmap font at a fractional point size (which smears
    // block-drawing glyphs and stems).
    private double _cellW = 8;
    private double _cellH = 16;
    // Independent horizontal/vertical zoom applied to the native render to
    // fill the viewport when ScaleToFit is on (1.0 = no zoom). Clamped to
    // [1, MaxScale] each. Scaled independently (not a single uniform factor)
    // so the grid fills the viewport exactly on both axes — a uniform factor
    // leaves a gap on whichever axis isn't the tighter constraint whenever the
    // window's aspect ratio doesn't match the native grid's.
    private double _fitScaleX = 1.0;
    private double _fitScaleY = 1.0;
    // Offscreen native-size buffer the grid renders into when zooming, then
    // gets blitted nearest-neighbour to the control bounds. Null when unscaled.
    private RenderTargetBitmap? _scaleBitmap;
    private bool _cursorBlinkOn = true;
    private DispatcherTimer? _blinkTimer;
    private Action? _onBufferChanged;

    // ----- Post-Enter "pending" overlay ---------------------------------
    // Without this, hitting Enter clears the local buffer immediately,
    // the overlay disappears, and the user sees a half-second of empty
    // space before the server's echo arrives and repaints the line.
    // We capture the flushed text + cursor position so the overlay
    // continues to render at the SAME spot — when the server echoes
    // back, the real cells underneath fill in with the same text and
    // there's no visual transition. Cleared on the next screen
    // update (which is almost always the echo itself).
    private string? _pendingFlushText;
    private int _pendingFlushRow;
    private int _pendingFlushCol;

    // Per-control cursor over the shared command-recall history. Built
    // lazily on first keystroke (AppServices.Current is live by then; it
    // may not be at design-time construction).
    private MudPlay.Services.CommandHistoryNavigator? _historyNav;
    private MudPlay.Services.CommandHistoryNavigator HistoryNav =>
        _historyNav ??= new(MudPlay.Services.AppServices.Current.CommandHistory);

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        _typeface = new Typeface(FontFamily);
        _typefaceBold = new Typeface(FontFamily, FontStyle.Normal, FontWeight.Bold);
        // Bitmap-style fonts (Mx437) need aliased rendering to avoid color
        // smearing across cell boundaries; subpixel AA fringes box-drawing chars.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Alias);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    static TerminalControl()
    {
        // Wire dependency-property change reactions: rebuild metrics when
        // the font changes, repaint when the emulator pointer changes.
        EmulatorProperty.Changed.AddClassHandler<TerminalControl>((c, e) => c.OnEmulatorChanged(
            (TerminalEmulator?)e.OldValue, (TerminalEmulator?)e.NewValue));
        FontFamilyProperty.Changed.AddClassHandler<TerminalControl>((c, _) => c.RecalculateMetrics());
        FontSizeProperty.Changed.AddClassHandler<TerminalControl>((c, _) => c.RecalculateMetrics());
        // Scaling inputs don't change the base metrics — only which scale factor
        // applies — so they re-fit rather than re-measure the base cell.
        ScaleToFitProperty.Changed.AddClassHandler<TerminalControl>((c, _) => c.ApplyScale());
        ViewportSizeProperty.Changed.AddClassHandler<TerminalControl>((c, _) => c.ApplyScale());
        SplashActiveProperty.Changed.AddClassHandler<TerminalControl>((c, e) => c.OnSplashActiveChanged((bool)e.NewValue!));
        // A live toggle of the animate flag while the splash is showing rebuilds
        // the animator so the change takes effect immediately.
        SplashAnimateProperty.Changed.AddClassHandler<TerminalControl>((c, _) =>
        {
            if (c.SplashActive) { c.OnSplashActiveChanged(false); c.OnSplashActiveChanged(true); }
        });
        AffectsRender<TerminalControl>(EmulatorProperty);
    }

    // Start/stop the splash animator as SplashActive flips. The animator owns a
    // standalone TerminalScreen sized to the live grid; on each frame it repaints
    // (we invalidate). Nothing here touches the emulator or scrollback.
    private void OnSplashActiveChanged(bool active)
    {
        if (active)
        {
            int cols = Emulator?.Screen.Cols ?? 80;
            int rows = Emulator?.Screen.Rows ?? 25;
            if (_splash is null)
            {
                _splash = new MudSplashAnimator(cols, rows, SplashAnimate);
                _splash.FrameAdvanced += OnSplashFrame;
            }
            else _splash.Resize(cols, rows);
            _splash.Start();
        }
        else if (_splash is { } sp)
        {
            sp.FrameAdvanced -= OnSplashFrame;
            sp.Dispose();
            _splash = null;
        }
        InvalidateVisual();
    }

    // FrameAdvanced fires on the UI thread (DispatcherTimer); repaint directly.
    private void OnSplashFrame() => InvalidateVisual();

    private void OnEmulatorChanged(TerminalEmulator? oldEm, TerminalEmulator? newEm)
    {
        // Detach from the previous emulator before subscribing to the new
        // one to avoid leaking handler references.
        if (oldEm is not null)
        {
            oldEm.ScreenUpdated -= OnScreenUpdated;
            oldEm.ScreenResized -= OnScreenResized;
        }
        if (newEm is not null)
        {
            newEm.ScreenUpdated += OnScreenUpdated;
            newEm.ScreenResized += OnScreenResized;
        }
        // A different emulator may carry a different grid size, changing the
        // natural dimensions the ScaleToFit math fits — re-fit before repaint.
        ApplyScale();
    }

    // ScreenUpdated may fire on any thread; invalidation must happen on the
    // UI thread. Also clears the post-Enter pending overlay — the server
    // just sent output (the most common case being the echo of the line
    // we just submitted), so the cell grid behind the overlay is now
    // accurate and the overlay can stop drawing.
    private void OnScreenUpdated()
    {
        if (_pendingFlushText is not null)
        {
            _pendingFlushText = null;
        }
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    // ScreenResized only fires on Emulator.Resize. Re-fit (the new cols/rows
    // change the natural grid the ScaleToFit math targets) then re-measure so
    // the canvas grows / shrinks to match the new cell grid.
    private void OnScreenResized() => Dispatcher.UIThread.Post(() =>
    {
        _splash?.Resize(Emulator?.Screen.Cols ?? 80, Emulator?.Screen.Rows ?? 25);
        ApplyScale();
    });

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RecalculateMetrics();
        // Cursor blink: toggle on/off twice a second.
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _blinkTimer.Tick += (_, _) =>
        {
            // Only repaint for the blink when there's actually a visible caret to
            // toggle. A hidden cursor (server-drawn full-screen forms) or the
            // splash has nothing to blink, so skip the otherwise-2Hz full-screen
            // repaint — Avalonia has no partial invalidate, so each blink would
            // otherwise redraw the whole grid for no visible change.
            if (SplashActive || Emulator?.Screen.CursorVisible != true) return;
            _cursorBlinkOn = !_cursorBlinkOn;
            InvalidateVisual();
        };
        _blinkTimer.Start();
        // Repaint the buffer overlay whenever the user types / backspaces
        // / flushes. Stored as a field so we can unsubscribe on detach
        // without leaking the strong handler reference into the buffer.
        if (InputBuffer is { } buf)
        {
            _onBufferChanged = () => Dispatcher.UIThread.Post(InvalidateVisual);
            buf.Changed += _onBufferChanged;
        }
        // Register the input core so keystrokes typed while a modeless dialog is
        // focused can be forwarded here (DialogKeyboardFallthrough → the router).
        MudPlay.Services.AppServices.CurrentOrNull?.TerminalInput
            .RegisterTerminal(HandleKeyCore, HandleTextCore);
        Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blinkTimer?.Stop();
        _blinkTimer = null;
        _scaleBitmap?.Dispose();
        _scaleBitmap = null;
        if (_onBufferChanged is not null && InputBuffer is { } buf)
        {
            buf.Changed -= _onBufferChanged;
            _onBufferChanged = null;
        }
        MudPlay.Services.AppServices.CurrentOrNull?.TerminalInput.UnregisterTerminal();
    }

    // Rebuild the native cell metrics at FontSize, then re-fit the window zoom.
    // Runs when the font or family changes.
    private void RecalculateMetrics()
    {
        _typeface = new Typeface(FontFamily);
        _typefaceBold = new Typeface(FontFamily, FontStyle.Normal, FontWeight.Bold);
        (_cellW, _cellH) = MeasureCell(FontSize);
        RecomputeScale();
        InvalidateMeasure();
        InvalidateVisual();
    }

    // Re-fit the window zoom to the current ScaleToFit / ViewportSize / grid
    // without re-measuring the native cell (those inputs don't move it).
    private void ApplyScale()
    {
        RecomputeScale();
        InvalidateMeasure();
        InvalidateVisual();
    }

    // Measure the width and height of the chosen font's "M" glyph at a given
    // point size and snap the result to whole pixels. Used as the per-cell box
    // size. Integer snapping keeps adjacent cell BG fills meeting exactly and
    // glyph advances aligned to the grid — without it, sub-pixel residue shows
    // up as 1px gaps/overlaps across cell boundaries.
    private (double w, double h) MeasureCell(double fontSize)
    {
        var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, fontSize, Brushes.White);
        return (Math.Max(1, Math.Round(probe.WidthIncludingTrailingWhitespace)),
                Math.Max(1, Math.Round(probe.Height)));
    }

    // Decide the horizontal/vertical window zoom. When ScaleToFit is off (or
    // the viewport is unknown) both are 1.0 (native). When on, each axis
    // scales independently to exactly match the viewport on that axis,
    // capped at MaxScale, never below 1 — so the grid always fills the whole
    // viewport with no gap on either axis, at the cost of the two axes
    // scaling by different factors (the bitmap stretches non-uniformly)
    // whenever the window's aspect ratio doesn't match the native grid's.
    //
    // The zoom is applied by upscaling the native render bitmap
    // nearest-neighbour (see Render) — NOT by rasterising the glyphs at a
    // larger point size. The CP437 face is a bitmap font: rasterising it at a
    // fractional size smears stems and leaves hairline gaps in the full-cell
    // block-drawing glyphs MajorMUD uses for borders/statline (the "color
    // bleed" fractional zoom produced). Nearest-neighbour upscaling introduces
    // no new colours and no gaps, so the grid fills any window size while
    // staying crisp.
    private void RecomputeScale()
    {
        double fitX = 1.0, fitY = 1.0;

        if (ScaleToFit)
        {
            Size vp = ViewportSize;
            int cols = Emulator?.Screen.Cols ?? 80;
            int rows = Emulator?.Screen.Rows ?? 25;
            double naturalW = _cellW * cols;
            double naturalH = _cellH * rows;
            if (vp.Width > 0 && vp.Height > 0 && naturalW > 0 && naturalH > 0)
            {
                fitX = Math.Clamp(vp.Width / naturalW, 1.0, MaxScale);
                fitY = Math.Clamp(vp.Height / naturalH, 1.0, MaxScale);
            }
        }

        _fitScaleX = fitX;
        _fitScaleY = fitY;
    }

    // Tell layout the control wants the native grid times the window zoom. At
    // zoom 1 that's the natural grid size; when fitting, it's the enlarged size
    // that fills the viewport (the render upscales the native bitmap to match).
    protected override Size MeasureOverride(Size availableSize)
    {
        var em = Emulator;
        int cols = em?.Screen.Cols ?? 80;
        int rows = em?.Screen.Rows ?? 25;
        return new Size(_cellW * cols * _fitScaleX, _cellH * rows * _fitScaleY);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var em = Emulator;
        var bounds = new Rect(Bounds.Size);
        // Clear background. (Done explicitly even though Background="Black"
        // is the window default — inside the ScrollViewer we may extend.)
        context.FillRectangle(Brushes.Black, bounds);

        // Startup splash: draw the standalone splash screen instead of the
        // emulator. Guarded so the normal path is untouched when inactive.
        if (SplashActive && _splash is { } sp)
        {
            if (_fitScaleX > 1.0 || _fitScaleY > 1.0) RenderScaledCells(context, sp.Screen);
            else DrawCells(context, sp.Screen);
            return;
        }

        if (em is null) return;

        // Zoomed: render the grid at native size into an offscreen bitmap, then
        // blit it nearest-neighbour to the (larger) control bounds. This keeps
        // the bitmap font on its native pixel grid — the upscale duplicates
        // whole pixels rather than re-rasterising glyphs at a fractional size,
        // so no colour bleed or block-glyph gaps. The unscaled path draws
        // straight to the context (no bitmap round-trip) to stay cheap.
        if (_fitScaleX > 1.0 || _fitScaleY > 1.0)
        {
            RenderScaled(context, em);
            return;
        }

        DrawScreen(context, em);
    }

    // Native-size render into the offscreen buffer, then a nearest-neighbour
    // blit to fill the control bounds.
    private void RenderScaled(DrawingContext context, TerminalEmulator em)
    {
        var screen = em.Screen;
        int nativeW = Math.Max(1, (int)Math.Round(_cellW * screen.Cols));
        int nativeH = Math.Max(1, (int)Math.Round(_cellH * screen.Rows));
        EnsureScaleBitmap(nativeW, nativeH);

        using (var bctx = _scaleBitmap!.CreateDrawingContext())
        {
            bctx.FillRectangle(Brushes.Black, new Rect(0, 0, nativeW, nativeH));
            DrawScreen(bctx, em);
        }

        var src = new Rect(0, 0, nativeW, nativeH);
        var dest = new Rect(0, 0, nativeW * _fitScaleX, nativeH * _fitScaleY);
        using (context.PushRenderOptions(new RenderOptions
        {
            BitmapInterpolationMode = BitmapInterpolationMode.None,
            EdgeMode = EdgeMode.Aliased,
        }))
        {
            context.DrawImage(_scaleBitmap!, src, dest);
        }
    }

    // Draw a standalone screen (the splash) with the same cell primitive as the
    // live terminal, batching same-attr runs per row. No input overlay / cursor.
    private void DrawCells(DrawingContext context, TerminalScreen screen)
    {
        for (int y = 0; y < screen.Rows; y++)
        {
            int x = 0;
            while (x < screen.Cols)
            {
                CellAttributes attr = screen[x, y].Attr;
                int x1 = x + 1;
                while (x1 < screen.Cols && screen[x1, y].Attr.Equals(attr)) x1++;
                DrawRun(context, screen, x, x1, y, attr);
                x = x1;
            }
        }
    }

    // Zoomed splash: native-size into the offscreen buffer, then nearest-neighbour
    // blit — mirrors RenderScaled but for an arbitrary screen.
    private void RenderScaledCells(DrawingContext context, TerminalScreen screen)
    {
        int nativeW = Math.Max(1, (int)Math.Round(_cellW * screen.Cols));
        int nativeH = Math.Max(1, (int)Math.Round(_cellH * screen.Rows));
        EnsureScaleBitmap(nativeW, nativeH);
        using (var bctx = _scaleBitmap!.CreateDrawingContext())
        {
            bctx.FillRectangle(Brushes.Black, new Rect(0, 0, nativeW, nativeH));
            DrawCells(bctx, screen);
        }
        var src = new Rect(0, 0, nativeW, nativeH);
        var dest = new Rect(0, 0, nativeW * _fitScaleX, nativeH * _fitScaleY);
        using (context.PushRenderOptions(new RenderOptions
        {
            BitmapInterpolationMode = BitmapInterpolationMode.None,
            EdgeMode = EdgeMode.Aliased,
        }))
        {
            context.DrawImage(_scaleBitmap!, src, dest);
        }
    }

    // (Re)allocate the offscreen buffer to the native grid size. RenderTarget
    // bitmaps aren't resizable, so a grid-size change drops and rebuilds it.
    private void EnsureScaleBitmap(int width, int height)
    {
        if (_scaleBitmap is { } b &&
            b.PixelSize.Width == width && b.PixelSize.Height == height)
            return;
        _scaleBitmap?.Dispose();
        _scaleBitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
    }

    // Draw the whole screen (cell runs + input overlay + caret) at native cell
    // size into the given context. Used directly for the unscaled path and into
    // the offscreen buffer for the zoomed path.
    private void DrawScreen(DrawingContext context, TerminalEmulator em)
    {
        var screen = em.Screen;

        // Resolve the local-line-edit overlay (buffered, not-yet-sent text)
        // up front — its length decides whether the whole screen needs to
        // scroll to keep the caret in view.
        //
        // Three render modes, in priority order:
        //   1. Live buffer non-empty → draw at CURRENT cursor.
        //   2. Live buffer empty but pending-flush captured → draw the
        //      just-Enter'd text at its CAPTURED cursor, so the visual
        //      stays seamless until the server's echo arrives.
        //   3. Neither → no overlay; caret defaults to current cursor.
        int caretCol = screen.CursorX;
        int caretRow = screen.CursorY;
        string? overlayText = null;
        int overlayStartCol = screen.CursorX;
        int overlayStartRow = screen.CursorY;
        // Character-mode (full-screen forms) suppresses the overlay
        // entirely — the server renders its own form + echo, so the
        // caret simply tracks the server cursor. Both overlay branches
        // gate on it so neither the live buffer nor a pending flush paints.
        bool charMode = InputBuffer is { CharacterMode: true };
        if (!charMode && InputBuffer is { Length: > 0 } buffer)
        {
            overlayText = buffer.Text;
        }
        else if (!charMode && _pendingFlushText is not null)
        {
            overlayText = _pendingFlushText;
            overlayStartCol = _pendingFlushCol;
            overlayStartRow = _pendingFlushRow;
        }

        // MajorMUD keeps its prompt on the bottom row, so a buffer long
        // enough to wrap spills past the last row. Rather than truncate the
        // tail (hiding what the user types) or float just the input upward
        // (it slides over static content — jarring), scroll the WHOLE screen
        // up by the overflow, exactly as character-mode server echo does.
        // The input then simply wraps onto the next line while the screen
        // scrolls to reveal it.
        int rowShift = 0;
        if (overlayText is not null)
        {
            int endCol = overlayStartCol;
            int endRow = overlayStartRow;
            foreach (char ch in overlayText)
            {
                if (endCol >= screen.Cols) { endCol = 0; endRow++; }
                endCol++;
            }
            rowShift = System.Math.Max(0, endRow - (screen.Rows - 1));
        }

        using (context.PushTransform(Matrix.CreateTranslation(0, -rowShift * _cellH)))
        {
            // Draw row by row, batching consecutive same-attribute cells into
            // a single "run" to reduce draw calls and keep BG fills contiguous.
            for (int y = 0; y < screen.Rows; y++)
            {
                int x = 0;
                while (x < screen.Cols)
                {
                    var startAttr = screen[x, y].Attr;
                    int runStart = x;
                    int runEnd = x;
                    while (runEnd < screen.Cols && screen[runEnd, y].Attr == startAttr)
                        runEnd++;

                    DrawRun(context, screen, runStart, runEnd, y, startAttr);
                    x = runEnd;
                }
            }

            // Paint the buffered overlay at the cursor, wrapping onto the
            // next line at the right edge. The cell grid behind it is
            // unchanged — when the user hits Enter and the server echoes the
            // line back, the echo writes real cells over the overlay area.
            // The shared scroll transform above keeps a bottom-row caret and
            // its wrapped tail on-screen.
            if (overlayText is not null)
            {
                int col = overlayStartCol;
                int row = overlayStartRow;
                foreach (char ch in overlayText)
                {
                    if (col >= screen.Cols)
                    {
                        // Wrap to the next line so a long buffer keeps
                        // rendering instead of clipping at the right edge.
                        col = 0;
                        row++;
                    }
                    double px = col * _cellW;
                    double py = row * _cellH;
                    // Black BG fill first so any server-painted cells
                    // underneath don't bleed through, then the glyph in the
                    // prompt foreground so the overlay reads inline.
                    context.FillRectangle(Brushes.Black,
                        new Rect(px, py, _cellW, _cellH));
                    var glyph = new FormattedText(
                        ch.ToString(),
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        FontSize,
                        Brushes.LightGray);
                    context.DrawText(glyph, new Point(px, py));
                    col++;
                }
                // Caret tracks the END of the LIVE buffer overlay (mode 1).
                // For pending overlay (mode 2) the caret stays at the
                // current cursor — the buffer is empty so the next typed
                // char would land there, NOT at the end of the pending
                // ghost text.
                if (InputBuffer is { Length: > 0 })
                {
                    caretCol = col;
                    caretRow = row;
                }
            }

            // Cursor caret — a thin horizontal bar at the bottom of its cell,
            // shown only when the screen says it's visible AND the blink is
            // "on". Position is the END of the buffer overlay (if any) so the
            // caret sits where the next typed char will land. The row test is
            // against the post-scroll position so a caret pushed to the
            // bottom line still draws.
            if (screen.CursorVisible && _cursorBlinkOn && caretRow - rowShift < screen.Rows)
            {
                var cx = caretCol * _cellW;
                var cy = caretRow * _cellH;
                // Buffer-full hint: when the local buffer is at the wire
                // cap (254 chars) the caret colour shifts so the user can
                // see further keystrokes are being dropped on the floor.
                IBrush caretBrush = InputBuffer is { IsFull: true } ? Brushes.OrangeRed : Brushes.LightGray;
                context.FillRectangle(caretBrush,
                    new Rect(cx, cy + _cellH * 0.85, _cellW, _cellH * 0.15));
            }
        }
    }

    // Render one horizontal run of same-attribute cells.
    private void DrawRun(DrawingContext context, TerminalScreen screen, int x0, int x1, int y, CellAttributes attr)
    {
        bool reverse = (attr.Flags & CellFlags.Reverse) != 0;
        bool bold = (attr.Flags & CellFlags.Bold) != 0;

        // Reverse video: just swap which color goes to fg vs bg.
        var fgColor = reverse ? attr.Background : attr.Foreground;
        var bgColor = reverse ? attr.Foreground : attr.Background;

        uint fgArgb = reverse
            ? AnsiPalette.ResolveBackground(fgColor)
            : AnsiPalette.ResolveForeground(fgColor, bold);
        uint bgArgb = reverse
            ? AnsiPalette.ResolveForeground(bgColor, bold)
            : AnsiPalette.ResolveBackground(bgColor);

        var fg = ToBrush(fgArgb);
        var bg = ToBrush(bgArgb);

        double left = x0 * _cellW;
        double top = y * _cellH;
        double width = (x1 - x0) * _cellW;
        // Single fill for the whole run's background.
        context.FillRectangle(bg, new Rect(left, top, width, _cellH));

        // SGR 8 — concealed: fill bg only; skip glyphs.
        if ((attr.Flags & CellFlags.Concealed) != 0) return;

        // Draw each cell individually at its exact pixel-aligned position.
        // Drawing a run as one FormattedText lets the font's advance widths
        // drift the glyph row away from the cell grid by fractions of a pixel,
        // which manifests as the visible "color bleed" between cells.
        var typeface = bold ? _typefaceBold : _typeface;
        for (int i = x0; i < x1; i++)
        {
            char ch = screen[i, y].Char;
            if (ch == ' ') continue;
            var ft = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, FontSize, fg);
            context.DrawText(ft, new Point(x0 == i ? left : i * _cellW, top));
        }

        // Underline — draw a 1px line along the bottom of the run.
        if ((attr.Flags & CellFlags.Underline) != 0)
            context.FillRectangle(fg, new Rect(left, top + _cellH - 1, width, 1));
    }

    // Brush cache: DrawRun resolves a fg + bg brush for every same-attr run, on
    // every repaint — a fresh ImmutableSolidColorBrush per run was pure allocation
    // churn under heavy output. The ARGB space is bounded (the 16 base + 256 xterm
    // palette entries), so caching them never grows unbounded. Render runs on the
    // UI thread only, so a plain Dictionary needs no lock.
    private static readonly Dictionary<uint, IBrush> _brushCache = new();

    private static IBrush ToBrush(uint argb)
    {
        if (_brushCache.TryGetValue(argb, out IBrush? cached)) return cached;
        var (r, g, b) = AnsiPalette.ToRgb(argb);
        IBrush brush = new ImmutableSolidColorBrush(Color.FromRgb(r, g, b));
        _brushCache[argb] = brush;
        return brush;
    }

    // ----- Input ---------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (HandleKeyCore(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // The terminal's key-handling core, callable both from OnKeyDown and from a
    // keystroke forwarded by another window (TerminalInputRouter). Returns true
    // when the key was consumed — a macro fired, the local line-editor handled it,
    // or it mapped to an escape sequence — and false for printable / unmapped keys
    // (those arrive as text via HandleTextCore instead).
    internal bool HandleKeyCore(Key key, KeyModifiers modifiers)
    {
        // Macro first — if the chord matches a user-defined keybind,
        // fire it via the dispatcher and consume the event so neither
        // MapKey nor OnTextInput see the keystroke. The dispatcher
        // returns false when no macro matches OR no sender is bound
        // yet (pre-telnet-connection), letting us fall through to the
        // regular terminal path.
        if (MudPlay.Services.AppServices.Current.MacroDispatcher
                .TryHandleKey(key, modifiers))
        {
            return true;
        }

        // Ctrl+V / Shift+Insert paste the clipboard into the input instead of
        // Ctrl+V mapping to the SYN control byte (0x16). The clipboard read is
        // async and a sync key handler can't await, so it runs detached; the key
        // is consumed either way.
        if ((key == Key.V && (modifiers & KeyModifiers.Control) != 0)
            || (key == Key.Insert && (modifiers & KeyModifiers.Shift) != 0))
        {
            _ = PasteFromClipboardAsync();
            return true;
        }

        // Local-line-edit intercept. Enter flushes the buffer + CR;
        // Backspace pops the last buffered char (and consumes the
        // event regardless so we never send 0x08 to the wire when in
        // line mode — per user: backspace just erases the buffer). Up /
        // Down recall previously-sent commands into the buffer. The
        // remaining special keys (Left/Right, F-keys, Ctrl+letter, Tab,
        // Escape) pass straight through via MapKey because they're
        // meaningful to the server immediately (login prompts, menu
        // navigation) and aren't part of any "line" the user is
        // composing. In character-mode (full-screen forms) the buffer is
        // bypassed entirely — Enter/Backspace/arrows fall through to
        // MapKey so the server's form reads each keystroke as it lands.
        if (InputBuffer is { CharacterMode: false } buf)
        {
            if (key == Key.Enter)
            {
                // Capture what we're flushing + where it's drawn so the
                // pending-overlay path can keep it visible until the
                // server's echo arrives. Skip the capture when the
                // buffer is empty (lone-CR Enter has no visual to
                // preserve) or when the emulator isn't ready yet.
                if (buf.Length > 0 && Emulator is { } em)
                {
                    _pendingFlushText = buf.Text;
                    _pendingFlushRow  = em.Screen.CursorY;
                    _pendingFlushCol  = em.Screen.CursorX;
                }
                // Record the typed line for Up/Down recall before the
                // flush clears the buffer; blank lines are dropped by
                // CommandHistory.Record itself.
                MudPlay.Services.AppServices.Current.CommandHistory.Record(buf.Text);
                HistoryNav.Reset();
                // Rapid-fire multi-command: a typed line carrying the macro
                // separators (';' / '^M') fans out into several wire lines the
                // same way macros do, so "sea n;sea n;n" sends three commands.
                // A separator-free line (incl. a blank Enter) yields a single
                // verbatim element, so ordinary input is unchanged. Flush the
                // buffer for its clear + Changed side effects, then emit each
                // line as its own CR-terminated wire send.
                string typedLine = buf.Text;
                _ = buf.FlushBytes();
                foreach (string wireLine in MudPlay.Services.MacroStore.SplitTypedInput(typedLine))
                    UserInput?.Invoke(System.Text.Encoding.Latin1.GetBytes(wireLine + "\r"));
                InvalidateVisual();
                return true;
            }
            if (key == Key.Back)
            {
                buf.Backspace();
                return true;
            }
            // Up / Down recall a previously-sent command into the buffer.
            // History navigation is reserved for line-mode; full-screen
            // forms (CharacterMode) skip this whole block, so their raw
            // CSI arrows still flow through MapKey below. Consumed even
            // with empty history so the arrow never leaks to the wire
            // mid-compose.
            if (key == Key.Up || key == Key.Down)
            {
                string? recalled = key == Key.Up
                    ? HistoryNav.Previous(buf.Text)
                    : HistoryNav.Next();
                if (recalled is not null)
                {
                    buf.Set(recalled);
                    InvalidateVisual();
                }
                return true;
            }
        }

        // Map special keys to escape sequences first; printable text is
        // delivered through OnTextInput instead.
        var bytes = MapKey(key, modifiers);
        if (bytes is not null)
        {
            UserInput?.Invoke(bytes);
            return true;
        }
        return false;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (HandleTextCore(e.Text))
            e.Handled = true;
    }

    // The terminal's typed-text core, callable both from OnTextInput and from a
    // character forwarded by another window (TerminalInputRouter). Returns true
    // when the text was consumed (buffered in line-mode, or sent in char-mode);
    // false for empty text.
    internal bool HandleTextCore(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        // Line-mode: route the typed chars into the local buffer
        // instead of straight to the wire. The render overlay paints
        // the buffer at the cursor on the next invalidation. Capped
        // silently at LocalInputBuffer.MaxLength (254 — MUD wire-level
        // line cap); chars past the cap are dropped. In character-mode
        // (full-screen forms) we fall through to the straight-to-wire
        // path so the server echoes each char itself.
        if (InputBuffer is { CharacterMode: false } buf)
        {
            buf.Append(text);
            // Typing a fresh char abandons history browsing — the next Up
            // starts again from the newest entry.
            HistoryNav.Reset();
            return true;
        }
        // Char-mode path (no buffer bound, OR LocalInputBuffer suspended
        // for a full-screen form). BBSes expect Latin-1 / 8-bit bytes,
        // not UTF-8. Encoding here keeps accented characters legible to
        // older servers.
        var bytes = System.Text.Encoding.Latin1.GetBytes(text);
        UserInput?.Invoke(bytes);
        return true;
    }

    // Read the clipboard and feed it into the input exactly as typed text would
    // arrive (line-mode buffers it; char-mode sends it). Newlines are folded to
    // the ';' command separator so a multi-line paste queues several commands the
    // Enter flush fans out, while a single-line paste just lands in the buffer.
    private async System.Threading.Tasks.Task PasteFromClipboardAsync()
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            if (await clipboard.TryGetDataAsync() is not { } data) return;
            string? text = await data.TryGetTextAsync();
            if (string.IsNullOrEmpty(text)) return;
            string input = text.Replace("\r\n", ";").Replace('\r', ';').Replace('\n', ';');
            HandleTextCore(input);
            InvalidateVisual();
        }
        catch { /* clipboard access can fail transiently; a dropped paste is a no-op */ }
    }

    // Translate non-text key presses into the byte sequence a real terminal
    // would emit. Returns null for keys we don't handle; OnTextInput will pick
    // up regular characters.
    private static byte[]? MapKey(Key key, KeyModifiers modifiers)
    {
        switch (key)
        {
            case Key.Enter: return new byte[] { 0x0D };
            case Key.Back: return new byte[] { 0x08 };
            case Key.Delete: return new byte[] { 0x7F };
            case Key.Escape: return new byte[] { 0x1B };
            case Key.Tab: return new byte[] { 0x09 };
            // Arrow / navigation keys — standard CSI sequences.
            case Key.Up: return new byte[] { 0x1B, (byte)'[', (byte)'A' };
            case Key.Down: return new byte[] { 0x1B, (byte)'[', (byte)'B' };
            case Key.Right: return new byte[] { 0x1B, (byte)'[', (byte)'C' };
            case Key.Left: return new byte[] { 0x1B, (byte)'[', (byte)'D' };
            case Key.Home: return new byte[] { 0x1B, (byte)'[', (byte)'H' };
            case Key.End: return new byte[] { 0x1B, (byte)'[', (byte)'F' };
            case Key.PageUp: return new byte[] { 0x1B, (byte)'[', (byte)'5', (byte)'~' };
            case Key.PageDown: return new byte[] { 0x1B, (byte)'[', (byte)'6', (byte)'~' };
            // F1–F4 use the older "SS3" form expected by most BBS software.
            case Key.F1: return new byte[] { 0x1B, (byte)'O', (byte)'P' };
            case Key.F2: return new byte[] { 0x1B, (byte)'O', (byte)'Q' };
            case Key.F3: return new byte[] { 0x1B, (byte)'O', (byte)'R' };
            case Key.F4: return new byte[] { 0x1B, (byte)'O', (byte)'S' };
        }

        // Ctrl+A..Z → control bytes 0x01..0x1A, the classic terminal
        // "control character" mapping.
        if ((modifiers & KeyModifiers.Control) != 0)
        {
            if (key >= Key.A && key <= Key.Z)
                return new byte[] { (byte)((int)key - (int)Key.A + 1) };
        }

        return null;
    }
}
