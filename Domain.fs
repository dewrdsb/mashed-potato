// 3 of 10 - the shapes everything else is written in
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
      NextMonitorVk : int option }

type Settings =
    { Launcher : Launcher
      Placement : Placement }
