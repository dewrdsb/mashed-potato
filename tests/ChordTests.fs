// What a keystroke means: the decisions the keyboard hook makes, without the hook.
// Each test is one key pressed in a known situation - chord armed or not, these
// modifiers held - and what it should come to.

module MashedPotato.Tests.ChordTests

open System.Windows.Forms
open Xunit

open MashedPotato
open MashedPotato.Chord

let private spotify =
    { Name = "Spotify"; ProcessName = "Spotify"; ExePaths = []; ShellFallback = ""; Tab = None }

let private leftZone =
    { Key = int Keys.H
      Name = "Left"
      Horizontal = Anchor.Start
      Vertical = Anchor.Middle
      Width = { Numerator = 2; Denominator = 3 }
      Height = { Numerator = 1; Denominator = 1 } }

let private ctrl = set [ Modifier.Ctrl ]
let private hyper = set [ Modifier.Ctrl; Modifier.Shift; Modifier.Alt ]
let private nothing : Set<Modifier> = Set.empty

/// Ctrl+K then Y for Spotify; Ctrl+Shift+Alt with H, Z, 1 and 0 for placement.
let private settings =
    Some
        { Launcher =
            { Prefix = { Modifiers = ctrl; Key = Some(int Keys.K) }
              Apps = [ int Keys.Y, spotify ]
              PassThroughIn = [| "mstsc" |] }
          Placement =
            { Prefix = { Modifiers = hyper; Key = None }
              Zones = [ leftZone ]
              NextMonitorVk = Some(int Keys.Z)
              CycleDisplayVk = Some(int Keys.D1)
              ApplyLayoutVk = Some(int Keys.D0)
              ApplyLayoutOnChange = None }
          Layouts = [] }

let private outsidePassThrough (_: string array) = false

/// A question the hook must not ask in this situation.
let private unaskable (_: 'a) : 'b = failwith "asked something the outcome does not depend on"

let private press armed (key: Keys) held =
    step settings armed (int key) false (fun () -> held) outsidePassThrough

let private passedOn armed = { Armed = armed; Swallow = false; Effects = [] }

// The launcher chord

[<Fact>]
let ``the launcher prefix arms the chord and raises the banner`` () =
    Assert.Equal({ Armed = true; Swallow = true; Effects = [ Effect.Prompt true ] }, press false Keys.K ctrl)

[<Fact>]
let ``a bound key while armed toggles its app and lowers the banner`` () =
    Assert.Equal(
        { Armed = false; Swallow = true; Effects = [ Effect.Prompt false; Effect.Toggle spotify ] },
        press true Keys.Y nothing)

[<Fact>]
let ``the chord's second key works with the prefix modifiers still held`` () =
    Assert.Equal(
        { Armed = false; Swallow = true; Effects = [ Effect.Prompt false; Effect.Toggle spotify ] },
        press true Keys.Y ctrl)

[<Theory>]
[<InlineData(Keys.Escape)>]
[<InlineData(Keys.K)>]
[<InlineData(Keys.Q)>]
let ``any other key while armed dismisses the chord and is swallowed`` (key: Keys) =
    Assert.Equal({ Armed = false; Swallow = true; Effects = [ Effect.Prompt false ] }, press true key ctrl)

[<Fact>]
let ``a bound key without the prefix first is just typing`` () =
    Assert.Equal(passedOn false, press false Keys.Y nothing)

[<Theory>]
[<InlineData(Keys.ControlKey)>]
[<InlineData(Keys.LControlKey)>]
[<InlineData(Keys.RShiftKey)>]
[<InlineData(Keys.Menu)>]
[<InlineData(Keys.LWin)>]
[<InlineData(Keys.RWin)>]
let ``a modifier on its own leaves an open chord open`` (key: Keys) =
    Assert.Equal(passedOn true, step settings true (int key) false unaskable unaskable)

[<Fact>]
let ``modifiers must match exactly - Ctrl+Shift+K is not Ctrl+K`` () =
    Assert.Equal(passedOn false, press false Keys.K (set [ Modifier.Ctrl; Modifier.Shift ]))

[<Fact>]
let ``the prefix key without its modifier is just typing`` () =
    Assert.Equal(passedOn false, press false Keys.K nothing)

[<Fact>]
let ``the launcher prefix is ignored in a pass-through app`` () =
    let outcome = step settings false (int Keys.K) false (fun () -> ctrl) (fun names -> names = [| "mstsc" |])
    Assert.Equal(passedOn false, outcome)

[<Fact>]
let ``the foreground app is only looked at when the prefix is pressed`` () =
    Assert.Equal(passedOn false, step settings false (int Keys.J) false (fun () -> ctrl) unaskable)
    Assert.Equal(passedOn false, step settings false (int Keys.K) false (fun () -> nothing) unaskable)

// Placement

[<Fact>]
let ``a zone key with the placement prefix held snaps the window`` () =
    Assert.Equal({ Armed = false; Swallow = true; Effects = [ Effect.SnapTo leftZone ] }, press false Keys.H hyper)

[<Theory>]
[<InlineData(Keys.Z)>]
[<InlineData(Keys.D1)>]
[<InlineData(Keys.D0)>]
let ``each placement key does its own thing`` (key: Keys) =
    let expected =
        match key with
        | Keys.Z -> Effect.NextMonitor
        | Keys.D1 -> Effect.CycleDisplay
        | _ -> Effect.ApplyLayout

    Assert.Equal({ Armed = false; Swallow = true; Effects = [ expected ] }, press false key hyper)

[<Fact>]
let ``placement wins over an open chord, and closes it`` () =
    Assert.Equal(
        { Armed = false; Swallow = true; Effects = [ Effect.Prompt false; Effect.SnapTo leftZone ] },
        press true Keys.H hyper)

[<Fact>]
let ``an unmapped key with the placement prefix closes an open chord`` () =
    Assert.Equal({ Armed = false; Swallow = true; Effects = [ Effect.Prompt false ] }, press true Keys.Q hyper)

[<Fact>]
let ``an unmapped key with the placement prefix is otherwise passed on`` () =
    Assert.Equal(passedOn false, press false Keys.Q hyper)

[<Fact>]
let ``a zone key needs exactly the placement modifiers`` () =
    Assert.Equal(passedOn false, press false Keys.H (set [ Modifier.Ctrl; Modifier.Alt ]))

[<Fact>]
let ``a placement key left unbound is passed on`` () =
    let unbound = settings |> Option.map (fun s -> { s with Placement = { s.Placement with NextMonitorVk = None } })
    Assert.Equal(passedOn false, step unbound false (int Keys.Z) false (fun () -> hyper) outsidePassThrough)

// What the hook stays out of

[<Fact>]
let ``injected keys are never taken`` () =
    Assert.Equal(passedOn false, step settings false (int Keys.K) true unaskable unaskable)
    Assert.Equal(passedOn true, step settings true (int Keys.Y) true unaskable unaskable)

[<Fact>]
let ``nothing is taken before a configuration has been read`` () =
    Assert.Equal(passedOn false, step None false (int Keys.K) false unaskable unaskable)
