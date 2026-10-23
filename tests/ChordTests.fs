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
          Layouts = []
          Remap = [] }

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

// Auto-repeat

[<Fact>]
let ``a held key repeating soon after its swallowed press is a repeat`` () =
    Assert.True(isRepeat (Some(int Keys.K, 1000u)) (int Keys.K) 1500u)

[<Fact>]
let ``a different key is never a repeat`` () =
    Assert.False(isRepeat (Some(int Keys.K, 1000u)) (int Keys.Y) 1010u)

[<Fact>]
let ``nothing is a repeat when no press was swallowed`` () =
    Assert.False(isRepeat None (int Keys.K) 1000u)

[<Fact>]
let ``a press long after the last one is fresh, whatever became of its release`` () =
    Assert.False(isRepeat (Some(int Keys.K, 1000u)) (int Keys.K) 5000u)

[<Fact>]
let ``a repeat is still recognised across the tick counter's wrap`` () =
    Assert.True(isRepeat (Some(int Keys.K, System.UInt32.MaxValue - 10u)) (int Keys.K) 20u)

// Telling a lost prefix apart

let private watchKey watching (key: Keys) pressedAt now injected swallowed held =
    watch settings watching (int key) pressedAt now injected swallowed held

let private holding (modifiers: Set<Modifier>) () = modifiers

[<Fact>]
let ``a hook running late says so`` () =
    let watched, notes = watchKey unwatched Keys.A 10_000u 10_400u false false (holding nothing)
    Assert.Equal(10_400u, watched.LastLateNote)
    Assert.Single(notes) |> ignore

[<Fact>]
let ``a stall is reported once, not once per key typed during it`` () =
    let watching = { unwatched with LastLateNote = 10_000u }
    Assert.Empty(snd (watchKey watching Keys.A 10_100u 10_400u false false (holding nothing)))

[<Fact>]
let ``a hook on time says nothing`` () =
    Assert.Equal((unwatched, []), watchKey unwatched Keys.A 10_000u 10_010u false false (holding nothing))

[<Fact>]
let ``injected keys are never late`` () =
    Assert.Equal((unwatched, []), watchKey unwatched Keys.A 0u 10_000u true false unaskable)

[<Fact>]
let ``a plain prefix key is remembered`` () =
    let watched, notes = watchKey unwatched Keys.K 10_000u 10_000u false false (holding nothing)
    Assert.Equal(Some 10_000u, watched.PlainPrefixAt)
    Assert.Empty(notes)

[<Fact>]
let ``a remapper's Ctrl arriving just after a plain prefix key is reported`` () =
    let watching = { unwatched with PlainPrefixAt = Some 10_000u }
    let watched, notes = watchKey watching Keys.LControlKey 10_020u 10_020u true false unaskable
    Assert.Equal(None, watched.PlainPrefixAt)
    Assert.Single(notes) |> ignore

[<Fact>]
let ``an injected Ctrl long after the prefix key is ordinary`` () =
    let watching = { unwatched with PlainPrefixAt = Some 10_000u }
    Assert.Equal((unwatched, []), watchKey watching Keys.LControlKey 11_000u 11_000u true false unaskable)

[<Fact>]
let ``another key between them clears the plain prefix`` () =
    let watching = { unwatched with PlainPrefixAt = Some 10_000u }
    Assert.Equal((unwatched, []), watchKey watching Keys.A 10_010u 10_010u false false unaskable)

[<Fact>]
let ``modifiers leave the plain prefix where it is`` () =
    let watching = { unwatched with PlainPrefixAt = Some 10_000u }
    Assert.Equal((watching, []), watchKey watching Keys.LShiftKey 10_010u 10_010u false false unaskable)

[<Fact>]
let ``the prefix key with an extra modifier held is reported`` () =
    let watched, notes = watchKey unwatched Keys.K 10_000u 10_000u false false (holding (set [ Modifier.Ctrl; Modifier.Win ]))
    Assert.Equal(None, watched.PlainPrefixAt)
    Assert.Contains("Ctrl+Win", Assert.Single(notes))

[<Fact>]
let ``the prefix key with unrelated modifiers is someone else's shortcut`` () =
    Assert.Equal((unwatched, []), watchKey unwatched Keys.K 10_000u 10_000u false false (holding (set [ Modifier.Alt ])))

[<Fact>]
let ``a prefix that opened the chord is not looked into`` () =
    Assert.Equal((unwatched, []), watchKey unwatched Keys.K 10_000u 10_000u false true unaskable)

[<Fact>]
let ``nothing is watched before a configuration has been read`` () =
    Assert.Equal((unwatched, []), watch None unwatched (int Keys.K) 10_000u 10_000u false false unaskable)

[<Fact>]
let ``releasing the swallowed key ends the watch on it`` () =
    Assert.Equal(None, released (Some(int Keys.K, 1000u)) (int Keys.K))

[<Fact>]
let ``releasing some other key leaves the watch be`` () =
    Assert.Equal(Some(int Keys.K, 1000u), released (Some(int Keys.K, 1000u)) (int Keys.LControlKey))

[<Fact>]
let ``a swallowed press is watched for repeats`` () =
    Assert.Equal(Some(int Keys.K, 2000u), pressed None (int Keys.K) 2000u false true)

[<Fact>]
let ``a key passed on takes over the repeating from a swallowed one`` () =
    Assert.Equal(None, pressed (Some(int Keys.K, 1000u)) (int Keys.A) 2000u false false)

[<Fact>]
let ``modifiers and injected keys leave the watch be`` () =
    let watching = Some(int Keys.K, 1000u)
    Assert.Equal(watching, pressed watching (int Keys.LControlKey) 2000u false false)
    Assert.Equal(watching, pressed watching (int Keys.A) 2000u true false)

// Remapping

let private capsToCtrl = Map [ int Keys.CapsLock, int Keys.LControlKey ]

/// A key from the keyboard, and one this program injected coming back round.
let private typed remapping (key: Keys) up time = remap capsToCtrl remapping (int key) up false false time
let private returned remapping (key: Keys) up = remap capsToCtrl remapping (int key) up true true 0u

let private ctrlDown = Some(Send.Key(int Keys.LControlKey, false))
let private ctrlUp = Some(Send.Key(int Keys.LControlKey, true))

/// Caps Lock pressed at 1000 and its Ctrl already back through the hook.
let private capsHeld = fst (returned (fst (typed unremapped Keys.CapsLock false 1000u)) Keys.LControlKey false)

[<Fact>]
let ``a remapped key sends the key it stands for`` () =
    let remapping, sending = typed unremapped Keys.CapsLock false 1000u
    Assert.Equal(ctrlDown, sending)
    Assert.Equal({ Held = Map [ int Keys.CapsLock, int Keys.LControlKey ]; Outstanding = 1; SentAt = 1000u }, remapping)

[<Fact>]
let ``a remapped key auto-repeats as the key it stands for`` () =
    Assert.Equal(ctrlDown, snd (typed capsHeld Keys.CapsLock false 1500u))

[<Fact>]
let ``releasing a remapped key releases the key it stands for`` () =
    let remapping, sending = typed capsHeld Keys.CapsLock true 2000u
    Assert.Equal(ctrlUp, sending)
    Assert.Empty(remapping.Held)

[<Fact>]
let ``a key typed once the stand-in is through goes on as it is`` () =
    Assert.Equal((capsHeld, None), typed capsHeld Keys.D false 1050u)

[<Fact>]
let ``a key that overtakes the stand-in is sent again behind it`` () =
    let pressed, _ = typed unremapped Keys.CapsLock false 1000u
    let overtaken, sending = typed pressed Keys.D false 1005u
    Assert.Equal(Some Send.Again, sending)
    Assert.Equal(2, overtaken.Outstanding)

    // Its release follows it, rather than overtaking it in turn.
    let released, sending = typed overtaken Keys.D true 1010u
    Assert.Equal(Some Send.Again, sending)

    // Ctrl, D and the release of D come back round in that order, and everything
    // after them goes on as it is.
    let caughtUp =
        [ Keys.LControlKey, false; Keys.D, false; Keys.D, true ]
        |> List.fold (fun remapping (key, up) -> fst (returned remapping key up)) released

    Assert.Equal(0, caughtUp.Outstanding)
    Assert.Equal(None, snd (typed caughtUp Keys.D false 1100u))

[<Fact>]
let ``an injection that never comes back stops holding keys up`` () =
    let pressed, _ = typed unremapped Keys.CapsLock false 1000u
    let remapping, sending = typed pressed Keys.D false 2500u
    Assert.Equal(None, sending)
    Assert.Equal(0, remapping.Outstanding)

[<Fact>]
let ``overtaking is still caught across the tick counter's wrap`` () =
    let pressed, _ = typed unremapped Keys.CapsLock false (System.UInt32.MaxValue - 5u)
    Assert.Equal(Some Send.Again, snd (typed pressed Keys.D false 10u))

[<Fact>]
let ``a held key is released even once the remap is gone from the configuration`` () =
    Assert.Equal(ctrlUp, snd (remap Map.empty capsHeld (int Keys.CapsLock) true false false 2000u))

[<Fact>]
let ``the release of a key pressed before the remap goes on as it is`` () =
    Assert.Equal((unremapped, None), typed unremapped Keys.CapsLock true 1000u)

[<Fact>]
let ``other programs' injections are left alone`` () =
    let pressed, _ = typed unremapped Keys.CapsLock false 1000u
    Assert.Equal((pressed, None), remap capsToCtrl pressed (int Keys.D) false true false 1005u)
    Assert.Equal((unremapped, None), remap capsToCtrl unremapped (int Keys.CapsLock) false true false 1005u)

[<Fact>]
let ``more coming back than went out is not counted below zero`` () =
    Assert.Equal(0, (fst (returned unremapped Keys.D false)).Outstanding)

[<Fact>]
let ``an INPUT is the size SendInput expects`` () =
    let expected = if System.IntPtr.Size = 8 then 40 else 28
    Assert.Equal(expected, System.Runtime.InteropServices.Marshal.SizeOf<Interop.INPUT>())

// Choosing a remap

let private remapAt name dock keys =
    { Name = name; When = { Dock = dock; Monitors = None }; Keys = keys }

let private home = remapAt "Home" (Some "WD22TB4") Map.empty
let private everywhere = remapAt "Everywhere" None capsToCtrl

let private docked (dock: string option) (conditions: Conditions) =
    conditions.Dock.IsNone || conditions.Dock = dock

[<Fact>]
let ``the first remap profile that fits is chosen`` () =
    Assert.Equal(Some home, chooseRemap [ home; everywhere ] (docked (Some "WD22TB4")))

[<Fact>]
let ``away from the desk a profile names, the next one fits`` () =
    Assert.Equal(Some everywhere, chooseRemap [ home; everywhere ] (docked (Some "WD25")))

[<Fact>]
let ``with no profile that fits, nothing is remapped`` () =
    Assert.Equal(None, chooseRemap [ home ] (docked None))

[<Fact>]
let ``arriving at a desk puts its remap in force and says so`` () =
    let keys, name, note = rechoose [ home; everywhere ] (docked (Some "WD22TB4")) (Some "Everywhere")
    Assert.Empty(keys)
    Assert.Equal(Some "Home", name)
    Assert.Contains("no keys are remapped", note.Value)

[<Fact>]
let ``leaving it puts the next remap in force`` () =
    let keys, name, note = rechoose [ home; everywhere ] (docked None) (Some "Home")
    Assert.Equal<Map<int, int>>(capsToCtrl, keys)
    Assert.Equal(Some "Everywhere", name)
    Assert.Contains("remapping 1 keys", note.Value)

[<Fact>]
let ``the remap already in force is not announced again`` () =
    let _, _, note = rechoose [ home; everywhere ] (docked None) (Some "Everywhere")
    Assert.Equal(None, note)

[<Fact>]
let ``no profile fitting is announced, and remaps nothing`` () =
    Assert.Equal((Map.empty, None, Some "No remap profile fits what is plugged in, so no keys are remapped."), rechoose [ home ] (docked None) (Some "Home"))

[<Fact>]
let ``no remap section at all is not announced`` () =
    Assert.Equal((Map.empty, None, None), rechoose [] unaskable (Some "Home"))
