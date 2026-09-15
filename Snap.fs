// 6 of 12 - moving and resizing the current window
//
// Zone geometry, the invisible border windows carry, and moving between monitors.
//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

module MashedPotato.Snap

open System
open System.Drawing
open System.Runtime.InteropServices
open System.Windows.Forms

open Interop

/// A window's rect includes the invisible resize border DWM keeps around it - 7 or
/// 8 pixels a side on this machine - so positioning by the window rect leaves a gap
/// down every edge. The difference between the window rect and the extended frame
/// bounds is that border: growing the target by it puts the *visible* edges where
/// they were asked to go.
let private allowingForBorder (hwnd: nativeint) (wanted: Rectangle) =
    let mutable window = RECT()
    let mutable frame = RECT()

    if GetWindowRect(hwnd, &window)
       && DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &frame, Marshal.SizeOf(typeof<RECT>)) = 0 then
        let left = frame.Left - window.Left
        let top = frame.Top - window.Top
        let right = window.Right - frame.Right
        let bottom = window.Bottom - frame.Bottom

        Rectangle(
            wanted.X - left,
            wanted.Y - top,
            wanted.Width + left + right,
            wanted.Height + top + bottom)
    else
        wanted

/// Shared with Layout, so both ways of placing a window round identically: 2/3 and
/// 1/3 of 1920 are 1280 and 640, and meet with nothing left over.
let internal span (fraction: Fraction) total =
    total * fraction.Numerator / fraction.Denominator

/// Anchoring End at `total - size` rather than at an accumulated offset is what
/// keeps complementary zones flush: a 1/3 zone anchored right starts at exactly
/// where a 2/3 zone anchored left finishes.
let private offset anchor total size =
    match anchor with
    | Anchor.Start -> 0
    | Anchor.Middle -> (total - size) / 2
    | Anchor.End -> total - size

/// Where a zone lands on a given monitor.
///
/// The area passed in is the monitor's *working area* - its full bounds minus the
/// taskbar and anything else docked to an edge. WinForms' Screen class provides
/// both; working area is almost always the one you want for placing windows.
let private target (zone: Zone) (area: Rectangle) =
    let width = span zone.Width area.Width
    let height = span zone.Height area.Height

    Rectangle(
        area.Left + offset zone.Horizontal area.Width width,
        area.Top + offset zone.Vertical area.Height height,
        width,
        height)

/// Where the window visually is, which is what gets moved. Falls back to the
/// window rect if DWM will not answer.
let private visibleFrame (hwnd: nativeint) =
    let mutable frame = RECT()

    if DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &frame, Marshal.SizeOf(typeof<RECT>)) = 0 then
        Rectangle(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top)
    else
        let mutable window = RECT()
        GetWindowRect(hwnd, &window) |> ignore
        Rectangle(window.Left, window.Top, window.Right - window.Left, window.Bottom - window.Top)

let private clamp low high value = max low (min high value)

/// Monitors in a stable order. Screen.AllScreens comes back in whatever order
/// EnumDisplayMonitors produced; sorting by device name (\\.\DISPLAY1,
/// \\.\DISPLAY2, ...) matches the numbering shown in Display Settings.
let internal orderedScreens () =
    Screen.AllScreens |> Array.sortBy (fun screen -> screen.DeviceName)

/// Where a window's visible frame lands when it moves between two monitors: same
/// size, same offset within the work area, clamped so a smaller destination cannot
/// push it off screen, and shrunk if it would not fit at all. Kept separate from
/// the Win32 calls so it can be exercised on its own.
let private movedBetween (source: Rectangle) (destination: Rectangle) (frame: Rectangle) =
    let width = min frame.Width destination.Width
    let height = min frame.Height destination.Height

    let left =
        destination.Left + (frame.Left - source.Left)
        |> clamp destination.Left (destination.Right - width)

    let top =
        destination.Top + (frame.Top - source.Top)
        |> clamp destination.Top (destination.Bottom - height)

    Rectangle(left, top, width, height)

/// Put a window's *visible* frame exactly here. Shared by snapping and by layouts,
/// which is what keeps the invisible-border correction in one place.
let place (hwnd: nativeint) (wanted: Rectangle) =
    // A maximized window ignores SetWindowPos geometry, so it has to come out of
    // that state first or the call appears to do nothing.
    if IsZoomed(hwnd) then ShowWindow(hwnd, SW_RESTORE) |> ignore

    let placed = allowingForBorder hwnd wanted

    SetWindowPos(hwnd, 0n, placed.X, placed.Y, placed.Width, placed.Height,
                 SWP_NOZORDER ||| SWP_NOACTIVATE)
    |> ignore

/// rcNormalPosition is in *workspace* coordinates: screen coordinates offset by the
/// origin of the primary monitor's work area. With the taskbar at the bottom, or
/// auto-hidden as it is here, that origin is (0, 0) and the two are identical -
/// which is exactly why the correction is easy to leave out and never notice. It
/// only starts to matter with the taskbar docked to the top or the left.
let private workspaceOrigin () =
    let primary = Screen.PrimaryScreen.WorkingArea
    primary.Left, primary.Top

/// Move a *minimized* window to another monitor by rewriting where it will come
/// back to, rather than by restoring it and moving it.
///
/// This is what makes "all of them on that monitor" mean anything for an app like
/// Word, where most windows are minimized most of the time. Un-minimizing them to
/// move them, then leaving them up, is a far bigger change to someone's desk than
/// naming a monitor asked for; leaving them out entirely means the instruction
/// quietly does not apply to the majority of the windows it named.
///
/// The rect here is the window rect, borders and all, rather than the visible frame
/// the other paths use. It is self-consistent: the offset within the work area is
/// preserved either way, and the invisible border simply travels with it.
let private restorePositionTo (hwnd: nativeint) (destination: Rectangle) =
    let mutable placement = WINDOWPLACEMENT()
    placement.length <- uint32 (Marshal.SizeOf(typeof<WINDOWPLACEMENT>))

    if GetWindowPlacement(hwnd, &placement) then
        let originX, originY = workspaceOrigin ()
        let normal = placement.rcNormalPosition

        let restored =
            Rectangle(
                normal.Left + originX,
                normal.Top + originY,
                normal.Right - normal.Left,
                normal.Bottom - normal.Top)

        let source = Screen.FromRectangle(restored).WorkingArea

        if source <> destination then
            let moved = movedBetween source destination restored

            placement.rcNormalPosition <-
                RECT(moved.Left - originX, moved.Top - originY,
                     moved.Right - originX, moved.Bottom - originY)

            // showCmd rides along untouched, so the window stays minimized.
            SetWindowPlacement(hwnd, &placement) |> ignore

/// Move a window to another monitor without deciding a size for it: same size, same
/// offset within the work area, clamped so a smaller destination cannot push it off
/// the edge.
///
/// A window already on that monitor is left completely alone rather than placed
/// where it already is. The difference is visible: `place` restores a maximized
/// window before moving it, so "moving" a maximized window nowhere would un-maximize
/// it - which is exactly the sort of thing a layout must not do to a window it was
/// asked to leave the size it is.
let toMonitor (hwnd: nativeint) (destination: Rectangle) =
    if IsIconic(hwnd) then
        restorePositionTo hwnd destination
    else
        let source = Screen.FromHandle(hwnd).WorkingArea

        if source <> destination then
            // A maximized window has to be restored before it will move, then
            // maximized again - on its new monitor, which is the point.
            let wasMaximized = IsZoomed(hwnd)
            if wasMaximized then ShowWindow(hwnd, SW_RESTORE) |> ignore

            place hwnd (movedBetween source destination (visibleFrame hwnd))

            if wasMaximized then ShowWindow(hwnd, SW_MAXIMIZE) |> ignore

/// Send the current window to the next monitor, wrapping past the last back to the
/// first. Its size is kept and its offset within the monitor preserved, clamped so
/// it cannot land off the edge of a smaller screen.
let toNextMonitor () =
    try
        let hwnd = GetForegroundWindow()
        let screens = orderedScreens ()

        if hwnd <> 0n && screens.Length > 1 then
            let current = Screen.FromHandle(hwnd)
            let index = screens |> Array.findIndex (fun screen -> screen.DeviceName = current.DeviceName)
            toMonitor hwnd screens.[(index + 1) % screens.Length].WorkingArea

        elif screens.Length <= 1 then
            Log.note "monitor" "Only one monitor, so there is nowhere to send the window."
    with error ->
        Log.write "monitor" error

/// The current window into a zone of its own monitor, full working height.
let toZone (zone: Zone) =
    try
        let hwnd = GetForegroundWindow()
        if hwnd <> 0n then place hwnd (target zone (Screen.FromHandle(hwnd).WorkingArea))
    with error ->
        Log.write "snap" error
