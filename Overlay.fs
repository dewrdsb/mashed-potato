// 9 of 11 - the on-screen banner
//
// The one piece of visible user interface.
//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

module internal MashedPotato.Overlay

open System
open System.Drawing
open System.Windows.Forms

open Interop

// A WinForms crash course, since this module is most of it:
//
//   Control  anything that occupies a rectangle - the base of the whole library.
//   Form     a top-level window (one HWND, owned by the thread that made it).
//   Label    a control that draws text.
//   Dock     layout: DockStyle.Fill makes a child fill its parent, so the caption
//            covers the banner without any coordinate arithmetic.
//   Padding  space reserved inside a container's edges. The parent's background
//            shows through it, which is how the orange border here is drawn -
//            no custom painting at all.
//
// Controls are painted on demand: Windows sends WM_PAINT, WinForms turns that into
// an OnPaint call. Painting uses GDI+ resources - fonts, brushes, pens - which are
// OS objects wrapped in IDisposable, and are the usual source of the kind of bug
// documented on applyFont below.

[<Literal>]
let private Message = "App Switcher"

[<Literal>]
let private BorderWidth = 20

/// Borderless, topmost, and never activated. WS_EX_NOACTIVATE earns its keep: if
/// this window took the foreground, App.activate's foreground test would see
/// Mashed Potato instead of the app being toggled and the minimize branch would never
/// fire. WS_EX_TOOLWINDOW keeps it out of Alt+Tab.
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
        parameters.ExStyle <- parameters.ExStyle ||| WS_EX_NOACTIVATE ||| WS_EX_TOOLWINDOW
        parameters

let mutable private banner : Form = null
let mutable private caption : Label = null
let mutable private fittedFor = Size.Empty

/// The area inside the border - what the caption actually gets to use.
let private inner (box: Size) =
    Size(box.Width - 2 * BorderWidth, box.Height - 2 * BorderWidth)

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

/// Fonts handed to the caption are never disposed, and are refitted only when the
/// size actually changes. Control.Font compares by value and ignores an assignment
/// that Equals the font already set, so a same-size refit leaves the label holding
/// the object we were about to release - and painting with a released font is a
/// GDI+ "Parameter is not valid" inside Label.OnPaint, which takes the process
/// down. Two monitors of equal size reach this: the banner moves between them, so
/// its origin changes while its size does not.
let private applyFont (box: Size) =
    if box <> fittedFor then
        caption.Font <- fitted box
        fittedFor <- box

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
    caption <-
        new Label(
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Cyan,
            ForeColor = Color.Black,
            Text = Message)

    banner <-
        new Banner(
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = true,
            // The form only shows through as the border: the caption is docked
            // over everything inside the padding.
            BackColor = Color.Orange,
            Padding = Padding(BorderWidth),
            Bounds = placement ())

    banner.Controls.Add(caption)
    applyFont (inner banner.Bounds.Size)
    banner.Handle |> ignore
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

        if banner.Bounds <> bounds then
            banner.Bounds <- bounds

        applyFont (inner bounds.Size)
        banner.Visible <- true
    else
        banner.Visible <- false
  with error ->
    Log.write "banner" error
