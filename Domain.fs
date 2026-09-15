// 3 of 12 - the shapes everything else is written in
//
// Pure data: no Windows, no JSON, no behaviour. Config parses into these, and the
// rest of the program reads them.
//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

namespace MashedPotato

/// An application the chord can toggle.
type Target =
    { /// Shown in the tray menu and in error balloons.
      Name : string

      /// Image name without .exe. Every process of that name counts, which is what
      /// makes this work for Chrome (23 processes) and Spotify (7).
      ProcessName : string

      /// Absolute candidates, tried in order; the first that exists is launched.
      ExePaths : string list

      /// Handed to ShellExecute when none of ExePaths exist. Either a URI scheme or
      /// a bare exe name, which the shell resolves through the App Paths registry key.
      ShellFallback : string }

/// A fraction of a monitor's width or height. Kept as two integers rather than a
/// float so complementary zones still meet exactly: 2/3 and 1/3 of 1920 are 1280 and
/// 640, with nothing left over.
type Fraction =
    { Numerator : int
      Denominator : int }

/// Where a zone sits on one axis.
[<RequireQualifiedAccess>]
type Anchor =
    | Start
    | Middle
    | End

/// A snap target: a fraction of the monitor on each axis, anchored to an edge, a
/// corner, or the centre. Ctrl+Shift+Alt+Key puts the current window here.
type Zone =
    { Key : int
      Name : string
      Horizontal : Anchor
      Vertical : Anchor
      Width : Fraction
      Height : Fraction }

/// Which monitor a layout puts a window on. Three spellings, because none of them
/// is right in every situation: an index is the obvious thing to write and moves
/// under you when monitors are rearranged, `primary` follows the machine, and a
/// name is exact but has to be looked up first (the tray menu lists them).
[<RequireQualifiedAccess>]
type MonitorRef =
    /// 1-based, in the order Snap numbers monitors - by adapter name, which is what
    /// Display Settings shows.
    | Index of int
    | Primary
    /// Any part of the monitor's EDID name, matched case-insensitively:
    /// "Dell P2222H (DP)" is found by "Dell", "P2222H" or "dell p2222h".
    | Named of string

/// A rectangle as fractions of a monitor's working area: where its left and top
/// edges start, and how much of each axis it covers. `Left` and `Top` may be zero,
/// which is the one way this differs from the fractions a Zone is built from.
type FractionRect =
    { Left : Fraction
      Top : Fraction
      Width : Fraction
      Height : Fraction }

/// Which of an app's windows a slot moves. Most apps have one worth arranging and
/// the front one is it; a document editor has as many as there are documents open,
/// and naming them in the file is not possible - they come and go.
[<RequireQualifiedAccess>]
type Instances =
    /// The window Ctrl+K would switch to: topmost, not minimized.
    | Front
    | All

/// One window in a layout: which app, which monitor, and where on it.
type Slot =
    { Target : Target
      Monitor : MonitorRef

      /// None is "put it on that monitor and leave it the size it is" - the whole
      /// point of which is not having to decide a size for a window you only care
      /// about the whereabouts of.
      Rect : FractionRect option

      Instances : Instances }

/// What has to be true for a profile to be the one that applies. Every condition
/// given must hold; a profile with none holds always, which is how the last one in
/// the list becomes the fallback.
type Conditions =
    { /// Part of the name of a device that is present - "WD25" finds "Dell Pro Dock
      /// WD25". Matched case-insensitively against the whole device tree.
      Dock : string option

      /// Exactly this many monitors attached.
      Monitors : int option }

/// One named arrangement and the circumstances it is for.
type LayoutProfile =
    { Name : string
      When : Conditions
      Slots : Slot list }

/// Everything the JSON file controls.
/// A modifier key held as part of a prefix.
[<RequireQualifiedAccess>]
type Modifier =
    | Ctrl
    | Shift
    | Alt
    | Win

/// What has to be pressed to reach a section's bindings. Two shapes:
///
///   modifiers and a key    "Ctrl+K"           a chord: pressed and released, then a
///                                             second key chooses the binding
///   modifiers only         "Ctrl+Shift+Alt"   held down while the binding key is hit
///
/// Modifiers must match *exactly*. A "Ctrl" prefix does not fire while Ctrl+Shift is
/// held: that combination belongs to whichever prefix actually asks for it.
type Prefix =
    { Modifiers : Set<Modifier>
      Key : int option }

/// Bringing an application up, focusing it, or minimizing it.
type Launcher =
    { Prefix : Prefix
      Apps : (int * Target) list
      PassThroughIn : string array }

/// Moving and resizing the current window.
type Placement =
    { Prefix : Prefix
      Zones : Zone list
      NextMonitorVk : int option
      CycleDisplayVk : int option
      ApplyLayoutVk : int option

      /// How long to wait for the hardware to settle after it changes, before
      /// applying the layout the new arrangement calls for. None leaves the
      /// keystroke as the only way to trigger one.
      ApplyLayoutOnChange : System.TimeSpan option }

type Settings =
    { Launcher : Launcher
      Placement : Placement

      /// Tried in order, first match wins - so the general ones go last. Empty
      /// unless the file has a layouts section.
      Layouts : LayoutProfile list }
