// Zone geometry and moving between monitors. Pure rectangle arithmetic, so most of
// it is stated as properties over every size of monitor rather than a few examples.

module MashedPotato.Tests.SnapTests

open System.Drawing
open FsCheck.Xunit
open Xunit

open MashedPotato

/// Any plausible working area: up to 8K across, anywhere on a virtual desktop
/// that may extend left of or above the primary monitor.
let private area (x: int) (y: int) (w: int) (h: int) =
    Rectangle(x % 8000, y % 8000, abs (w % 7680) + 1, abs (h % 4320) + 1)

/// Any fraction n/d with 1 <= n <= d <= 12, the range anyone writes.
let private fractionFrom (n: int) (d: int) =
    let denominator = abs (d % 12) + 1
    { Numerator = abs (n % denominator) + 1; Denominator = denominator }

let private zone horizontal vertical width height =
    { Key = 0; Name = "test"; Horizontal = horizontal; Vertical = vertical; Width = width; Height = height }

let private whole = { Numerator = 1; Denominator = 1 }

[<Fact>]
let ``thirds of 1920 meet exactly`` () =
    Assert.Equal(1280, Snap.span { Numerator = 2; Denominator = 3 } 1920)
    Assert.Equal(640, Snap.span { Numerator = 1; Denominator = 3 } 1920)

[<Fact>]
let ``the left two thirds of a monitor`` () =
    let placed = Snap.target (zone Anchor.Start Anchor.Middle { Numerator = 2; Denominator = 3 } whole) (Rectangle(-1920, 0, 1920, 1040))
    Assert.Equal(Rectangle(-1920, 0, 1280, 1040), placed)

[<Fact>]
let ``a centred half`` () =
    let half = { Numerator = 1; Denominator = 2 }
    let placed = Snap.target (zone Anchor.Middle Anchor.Middle half half) (Rectangle(0, 0, 1920, 1080))
    Assert.Equal(Rectangle(480, 270, 960, 540), placed)

[<Property>]
let ``a zone always lies inside the working area`` (x, y, w, h, n1, d1, n2, d2) =
    let screen = area x y w h

    [ Anchor.Start; Anchor.Middle; Anchor.End ]
    |> List.allPairs [ Anchor.Start; Anchor.Middle; Anchor.End ]
    |> List.forall (fun (horizontal, vertical) ->
        screen.Contains(Snap.target (zone horizontal vertical (fractionFrom n1 d1) (fractionFrom n2 d2)) screen))

[<Property>]
let ``an end-anchored zone finishes exactly at the edge`` (x, y, w, h, n, d) =
    let screen = area x y w h
    let placed = Snap.target (zone Anchor.End Anchor.End (fractionFrom n d) (fractionFrom n d)) screen
    placed.Right = screen.Right && placed.Bottom = screen.Bottom

/// Complementary zones - a left n/d beside a right (d-n)/d - never overlap, and
/// meet exactly when the width divides evenly. When it does not, rounding both
/// down leaves at most one pixel between them: 1/3 and 2/3 of 1000 are 333 and 666.
[<Property>]
let ``complementary zones never overlap and leave at most a pixel`` (x, y, w, h, n, d) =
    let screen = area x y w h
    let left = fractionFrom n d

    // A whole-width zone has nothing to sit beside.
    left.Numerator = left.Denominator
    || (let rest = { left with Numerator = left.Denominator - left.Numerator }
        let leftPart = Snap.target (zone Anchor.Start Anchor.Middle left whole) screen
        let rightPart = Snap.target (zone Anchor.End Anchor.Middle rest whole) screen
        let gap = rightPart.Left - leftPart.Right
        gap = 0 || gap = 1)

[<Property>]
let ``a window moved between monitors always lands on the destination`` (sx, sy, sw, sh, dx, dy, dw, dh, fx, fy, fw, fh) =
    let source = area sx sy sw sh
    let destination = area dx dy dw dh
    let frame = Rectangle(source.Left + fx % 2000, source.Top + fy % 2000, abs (fw % 4000) + 1, abs (fh % 4000) + 1)
    destination.Contains(Snap.movedBetween source destination frame)

[<Fact>]
let ``a window keeps its offset and size when the destination has room`` () =
    let moved = Snap.movedBetween (Rectangle(0, 0, 1920, 1040)) (Rectangle(1920, 0, 2560, 1400)) (Rectangle(100, 50, 800, 600))
    Assert.Equal(Rectangle(2020, 50, 800, 600), moved)

[<Fact>]
let ``a window too big for the destination is shrunk to fit`` () =
    let moved = Snap.movedBetween (Rectangle(0, 0, 2560, 1400)) (Rectangle(-1280, 0, 1280, 1000)) (Rectangle(200, 100, 2000, 1200))
    Assert.Equal(Rectangle(-1280, 0, 1280, 1000), moved)
