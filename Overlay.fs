// 11 of 13 - the on-screen banner
//
// The one piece of visible user interface.
//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

module internal MashedPotato.Overlay

open System.ComponentModel
open System.Drawing
open System.Drawing.Drawing2D
open System.Drawing.Imaging
open System.Drawing.Text
open System.Windows.Forms

open Interop

// The banner is a *layered* window: rather than painting itself through WM_PAINT
// like an ordinary WinForms form, it hands Windows a finished 32-bit bitmap -
// alpha channel included - and DWM composites that over the desktop. That is what
// makes the rounded corners smooth. A window region, the usual way to round a
// window, can only include or exclude whole pixels, so its arcs come out stepped.
//
// The whole picture - orange body, cyan face, text - is drawn with GDI+ into that
// bitmap, antialiased throughout, so there are no child controls at all. GDI+
// resources - fonts, brushes, paths, bitmaps - are OS objects wrapped in
// IDisposable, hence all the `use`.

[<Literal>]
let private Message = "App Switcher"

[<Literal>]
let private BorderWidth = 20

/// Corner radius of the banner itself.
[<Literal>]
let private CornerRadius = 26

/// Corner radius of the cyan area inside the border.
///
/// Outer minus the border width is what makes the two arcs concentric, and concentric
/// is what keeps the orange ring the same 20px thickness the whole way round. Any
/// larger and the border pinches thin at the corners; any smaller and it bulges.
[<Literal>]
let private InnerCornerRadius = CornerRadius - BorderWidth

/// Borderless, topmost, and never activated. WS_EX_NOACTIVATE earns its keep: if
/// this window took the foreground, App.activate's foreground test would see
/// Mashed Potato instead of the app being toggled and the minimize branch would never
/// fire. WS_EX_TOOLWINDOW keeps it out of Alt+Tab, and WS_EX_LAYERED is what lets
/// it take a bitmap with an alpha channel - see present.
type private Banner() =
    inherit Form()

    // Two protected members overridden to reach settings WinForms does not expose
    // as properties. Subclassing to override a protected member is the normal .NET
    // way to customise a framework class - closer to OO inheritance than anything
    // in idiomatic F#, but this is the library's idiom and it is what it expects.

    /// Consulted by Form.SetVisibleCore: when true, showing the window uses
    /// SW_SHOWNOACTIVATE, so it appears without taking focus.
    override _.ShowWithoutActivation = true

    /// CreateParams is the bundle of arguments WinForms passes to CreateWindowEx
    /// when the underlying HWND is first made. Overriding it is the only way to
    /// set extended window styles that have no WinForms property.
    override this.CreateParams =
        let parameters = base.CreateParams
        parameters.ExStyle <- parameters.ExStyle ||| WS_EX_NOACTIVATE ||| WS_EX_TOOLWINDOW ||| WS_EX_LAYERED
        parameters

let mutable private banner : Form = null

/// The last picture drawn, and the size it was drawn for. Re-drawn only when the
/// size changes: the banner moves between monitors far more often than it resizes.
let mutable private picture : Bitmap = null

/// The largest Segoe UI that still leaves some air around the text. Sized in
/// pixels rather than points so it comes out the same at any display scaling.
let private fitted (box: Size) =
    // The banner is far wider than "App Switcher" is at any size, so height is
    // what actually binds; leaving only ~18% of it as padding is what makes the
    // text look like it fills the window rather than floating in it.
    let maxWidth = box.Width * 85 / 100
    let maxHeight = box.Height * 82 / 100

    let rec shrink (pixels: float32) =
        let candidate = new Font("Segoe UI", pixels, FontStyle.Bold, GraphicsUnit.Pixel)
        let measured = TextRenderer.MeasureText(Message, candidate)

        if pixels <= 12f || (measured.Width <= maxWidth && measured.Height <= maxHeight) then
            candidate
        else
            candidate.Dispose()
            shrink (pixels * 0.92f)

    shrink (float32 box.Height * 0.6f)

/// A rectangle with all four corners rounded to the same radius, as a path GDI+
/// can fill with antialiased edges. Each AddArc is one quarter of a circle of
/// diameter 2 * radius, sitting in its corner; the path joins them with the
/// straight sides.
let private rounded (area: RectangleF) (radius: float32) =
    let path = new GraphicsPath()
    let diameter = radius * 2f
    path.AddArc(area.Left, area.Top, diameter, diameter, 180f, 90f)
    path.AddArc(area.Right - diameter, area.Top, diameter, diameter, 270f, 90f)
    path.AddArc(area.Right - diameter, area.Bottom - diameter, diameter, diameter, 0f, 90f)
    path.AddArc(area.Left, area.Bottom - diameter, diameter, diameter, 90f, 90f)
    path.CloseFigure()
    path

/// Draws the whole banner at the given size. Everything outside the orange is left
/// fully transparent, and the antialiased edge pixels partly so, which is what
/// lets the corners fade into whatever is behind them.
let private draw (box: Size) =
    let image = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppPArgb)
    use graphics = Graphics.FromImage(image)
    graphics.Clear(Color.Transparent)
    graphics.SmoothingMode <- SmoothingMode.AntiAlias
    // GDI+ otherwise treats a pixel's centre as its corner, which leaves a fill
    // from 0 to Width only half-covering the pixels along its straight edges - a
    // faint translucent line all the way round.
    graphics.PixelOffsetMode <- PixelOffsetMode.HighQuality
    // Unhinted antialiasing for the text. GDI, which is what a Label draws with,
    // does not smooth text this large at all; hinting is for small text and only
    // distorts glyphs at display sizes.
    graphics.TextRenderingHint <- TextRenderingHint.AntiAlias

    let outside = RectangleF(0f, 0f, float32 box.Width, float32 box.Height)
    let inside = RectangleF.Inflate(outside, float32 -BorderWidth, float32 -BorderWidth)

    use orange = new SolidBrush(Color.Orange)
    use body = rounded outside (float32 CornerRadius)
    graphics.FillPath(orange, body)

    use cyan = new SolidBrush(Color.Cyan)
    use face = rounded inside (float32 InnerCornerRadius)
    graphics.FillPath(cyan, face)

    use font = fitted (Size.Truncate inside.Size)
    use black = new SolidBrush(Color.Black)
    // NoWrap: GDI+ measures a little wider than the GDI fit in `fitted` does, and
    // should never break "App Switcher" over two lines because of it.
    use format =
        new StringFormat(
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap)
    graphics.DrawString(Message, font, black, inside, format)
    image

/// Puts the banner's picture on screen at `bounds`, drawing a new one first if the
/// size has changed.
///
/// UpdateLayeredWindow is called on every show rather than only after a redraw:
/// it sets position and size along with the picture, so the window can never be
/// left one size with a bitmap of another - which a DPI change on the way to a
/// different monitor could otherwise do behind our back.
let private present (bounds: Rectangle) =
    if isNull picture || picture.Size <> bounds.Size then
        let fresh = draw bounds.Size
        if not (isNull picture) then picture.Dispose()
        picture <- fresh

    let screen = GetDC 0n
    let memory = CreateCompatibleDC screen
    // A GDI copy of the picture. GetHbitmap premultiplies the alpha, which is the
    // form UpdateLayeredWindow requires; the argument is only the colour used for
    // fully transparent pixels.
    let hbitmap = picture.GetHbitmap(Color.FromArgb 0)
    let previous = SelectObject(memory, hbitmap)

    try
        let mutable position = POINT(bounds.X, bounds.Y)
        let mutable size = SIZE(bounds.Width, bounds.Height)
        let mutable origin = POINT(0, 0)
        let mutable blend = BLENDFUNCTION(AC_SRC_OVER, 0uy, 255uy, AC_SRC_ALPHA)

        if not (UpdateLayeredWindow(banner.Handle, screen, &position, &size, memory, &origin, 0u, &blend, ULW_ALPHA)) then
            // Picks up the error code SetLastError saved; setVisible logs it.
            raise (Win32Exception())
    finally
        // Undone in reverse: the displaced bitmap back into the DC, then each
        // handle returned the way it was obtained.
        SelectObject(memory, previous) |> ignore
        DeleteObject hbitmap |> ignore
        DeleteDC memory |> ignore
        ReleaseDC(0n, screen) |> ignore

/// Across the bottom of whichever screen holds the foreground window: three
/// quarters of the width, centred, one fifth of the height. WorkingArea rather
/// than Bounds, so it sits above the taskbar instead of over it.
let private placement () =
    let area = Screen.FromHandle(GetForegroundWindow()).WorkingArea
    let width = area.Width * 3 / 4
    let height = area.Height / 5
    Rectangle(area.Left + (area.Width - width) / 2, area.Bottom - height, width, height)

/// Built once at startup. Creating a window inside the hook callback would be slow,
/// and a low-level hook has to return promptly or Windows stops calling it.
let create () =
    banner <-
        new Banner(
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = true,
            Bounds = placement ())

    // A layered window shows nothing at all until it has been given a picture, and
    // giving it one needs a real HWND - so the handle first, then the picture.
    banner.Handle |> ignore
    present banner.Bounds
    banner

/// Setting Visible is enough to keep this from stealing focus: Form.SetVisibleCore
/// checks ShowWithoutActivation and uses SW_SHOWNOACTIVATE.
let setVisible visible =
  try
    if isNull banner then
        ()
    elif visible then
        // The screen holding the foreground window can differ from chord to chord.
        let bounds = placement ()

        // Kept in step with what present does, so WinForms' own idea of where the
        // window is never disagrees with where it actually is.
        if banner.Bounds <> bounds then
            banner.Bounds <- bounds

        present bounds
        banner.Visible <- true
    else
        banner.Visible <- false
  with error ->
    Log.write "banner" error
