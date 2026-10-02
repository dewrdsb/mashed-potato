// Reading mashedpotato.json: what a good file turns into, and what each kind of
// mistake in one is reported as. The messages are the interface here - they are
// the only thing a person editing the file sees - so the tests pin the part of
// each one that says what is wrong.

module MashedPotato.Tests.ConfigTests

open System
open System.IO
open System.Windows.Forms
open Xunit

open MashedPotato

/// The smallest file that parses, with the parts the tests vary left as holes.
let private config (launcherPrefix: string) (placementPrefix: string) (zone: string) (rest: string) =
    $$"""
    {
      "launcher": {
        "prefix": {{launcherPrefix}},
        "apps": [
          { "key": "Y", "name": "Spotify", "processName": "Spotify.exe", "shellFallback": "spotify:" }
        ]
      },
      "placement": {
        "prefix": {{placementPrefix}},
        "zones": [ {{zone}} ]
        {{rest}}
      }
    }
    """

let private leftZone = """{ "key": "H", "name": "Left", "side": "left", "width": "2/3" }"""

let private minimal = config "\"Ctrl+K\"" "\"Ctrl+Shift+Alt\"" leftZone ""

/// A file with one layout in it, and the key that applies it.
let private withLayout (window: string) (extra: string) =
    $$"""
    {
      "launcher": {
        "prefix": "Ctrl+K",
        "apps": [ { "key": "Y", "name": "Spotify", "processName": "Spotify" } ]
      },
      "placement": { "prefix": "Ctrl+Shift+Alt", "applyLayout": "0" {{extra}} },
      "layouts": [ { "name": "Desk", "windows": [ {{window}} ] } ]
    }
    """

let private failsWith (fragment: string) (json: string) =
    let error = Assert.ThrowsAny<exn>(Action(fun () -> Config.parse json |> ignore))
    Assert.Contains(fragment, error.Message)

[<Fact>]
let ``the shipped configuration parses`` () =
    let shipped = Path.Combine(__SOURCE_DIRECTORY__, "..", "mashedpotato.json")
    let settings = Config.parse (File.ReadAllText shipped)
    Assert.NotEmpty settings.Launcher.Apps
    Assert.NotEmpty settings.Placement.Zones

[<Fact>]
let ``a minimal file parses into the domain`` () =
    let settings = Config.parse minimal

    Assert.Equal<Set<Modifier>>(set [ Modifier.Ctrl ], settings.Launcher.Prefix.Modifiers)
    Assert.Equal(Some(int Keys.K), settings.Launcher.Prefix.Key)
    Assert.Equal<Set<Modifier>>(set [ Modifier.Ctrl; Modifier.Shift; Modifier.Alt ], settings.Placement.Prefix.Modifiers)
    Assert.Equal(None, settings.Placement.Prefix.Key)
    Assert.Empty settings.Layouts

    let key, spotify = List.exactlyOne settings.Launcher.Apps
    Assert.Equal(int Keys.Y, key)
    Assert.Equal("Spotify", spotify.ProcessName) // .exe taken off
    Assert.Equal(None, spotify.Tab)

    let zone = List.exactlyOne settings.Placement.Zones
    Assert.Equal(Anchor.Start, zone.Horizontal)
    Assert.Equal(Anchor.Middle, zone.Vertical)
    Assert.Equal({ Numerator = 2; Denominator = 3 }, zone.Width)
    Assert.Equal({ Numerator = 1; Denominator = 1 }, zone.Height) // omitted means the whole axis

[<Fact>]
let ``the object form of a prefix means the same as the string form`` () =
    let objectForm = config """{ "key2": "K", "key1": "Ctrl" }""" "\"Ctrl+Shift+Alt\"" leftZone ""
    Assert.Equal((Config.parse minimal).Launcher.Prefix, (Config.parse objectForm).Launcher.Prefix)

[<Fact>]
let ``comments and trailing commas are allowed`` () =
    let commented = minimal.Replace("\"apps\"", "// the apps\n\"apps\"").Replace("\"spotify:\" }", "\"spotify:\", }")
    Config.parse commented |> ignore

[<Fact>]
let ``an empty file is reported as one`` () =
    "null" |> failsWith "the file is empty"

[<Theory>]
[<InlineData("\"Ctrl\"", "needs a key as well as modifiers")>]
[<InlineData("\"K\"", "at least one modifier")>]
[<InlineData("\"Ctrl+K+L\"", "more than one non-modifier key")>]
[<InlineData("\"Ctrl+Fnord\"", "'Fnord' is not a key")>]
[<InlineData("\"Ctrl+12\"", "'12' is not a key")>]
[<InlineData("42", "must be a string such as Ctrl+K")>]
let ``a bad launcher prefix says what is wrong`` (prefix: string, fragment: string) =
    config prefix "\"Ctrl+Shift+Alt\"" leftZone "" |> failsWith fragment

[<Fact>]
let ``a held prefix with a key in it is refused`` () =
    config "\"Ctrl+K\"" "\"Ctrl+Shift+H\"" leftZone "" |> failsWith "must be modifiers only - remove H"

[<Theory>]
[<InlineData("sideways", "1/2", "'sideways' is not a side")>]
[<InlineData("left", "0", "0/1 is not usable")>]
[<InlineData("left", "4/3", "4/3 is not usable")>]
[<InlineData("left", "1/0", "1/0 is not usable")>]
[<InlineData("left", "half", "'half' is not a fraction")>]
[<InlineData("left", "1/2/3", "'1/2/3' is not a fraction")>]
let ``a bad zone says what is wrong`` (side: string, width: string, fragment: string) =
    let zone = $$"""{ "key": "H", "name": "Left", "side": "{{side}}", "width": "{{width}}" }"""
    config "\"Ctrl+K\"" "\"Ctrl+Shift+Alt\"" zone "" |> failsWith fragment

[<Theory>]
[<InlineData("topleft", "Start", "Start")>]
[<InlineData("Bottom-Right", "End", "End")>]
[<InlineData("centre", "Middle", "Middle")>]
[<InlineData("top", "Middle", "Start")>]
let ``sides are read loosely`` (side: string, horizontal: string, vertical: string) =
    let zone = $$"""{ "key": "H", "name": "Z", "side": "{{side}}", "width": "1/2" }"""
    let parsed = (Config.parse (config "\"Ctrl+K\"" "\"Ctrl+Shift+Alt\"" zone "")).Placement.Zones |> List.exactlyOne
    Assert.Equal(horizontal, sprintf "%A" parsed.Horizontal)
    Assert.Equal(vertical, sprintf "%A" parsed.Vertical)

[<Fact>]
let ``an app needs a process name`` () =
    minimal.Replace("\"processName\": \"Spotify.exe\",", "") |> failsWith "processName is required"

[<Fact>]
let ``optional placement keys can be bound or left as none`` () =
    let placement = (Config.parse (config "\"Ctrl+K\"" "\"Ctrl+Shift+Alt\"" leftZone """, "nextMonitor": "Z", "cycleDisplay": "none" """)).Placement
    Assert.Equal(Some(int Keys.Z), placement.NextMonitorVk)
    Assert.Equal(None, placement.CycleDisplayVk)
    Assert.Equal(None, placement.ApplyLayoutVk)

// Layouts

[<Fact>]
let ``a layout reads its window slots`` () =
    let settings = Config.parse (withLayout """{ "app": "spotify", "monitor": 2, "rect": "1/3, 0, 2/3, 1", "instances": "all" }""" "")
    let profile = List.exactlyOne settings.Layouts
    let slot = List.exactlyOne profile.Slots

    Assert.Equal("Desk", profile.Name)
    Assert.Equal({ Dock = None; Monitors = None }, profile.When)
    Assert.Equal("Spotify", slot.Target.Name) // matched case-insensitively
    Assert.Equal(MonitorRef.Index 2, slot.Monitor)
    Assert.Equal(Instances.All, slot.Instances)
    Assert.Equal(
        Some { Left = { Numerator = 1; Denominator = 3 }
               Top = { Numerator = 0; Denominator = 1 }
               Width = { Numerator = 2; Denominator = 3 }
               Height = { Numerator = 1; Denominator = 1 } },
        slot.Rect)

[<Theory>]
[<InlineData("\"primary\"")>]
[<InlineData("\"PRIMARY\"")>]
let ``primary names the primary monitor`` (monitor: string) =
    let slot = (Config.parse (withLayout $$"""{ "app": "Spotify", "monitor": {{monitor}} }""" "")).Layouts.Head.Slots.Head
    Assert.Equal(MonitorRef.Primary, slot.Monitor)
    Assert.Equal(None, slot.Rect)
    Assert.Equal(Instances.Front, slot.Instances)

[<Theory>]
[<InlineData("\"Dell P2222H\"", "Dell P2222H")>]
[<InlineData("\" Dell \"", "Dell")>]
let ``anything else names a monitor`` (monitor: string, name: string) =
    let slot = (Config.parse (withLayout $$"""{ "app": "Spotify", "monitor": {{monitor}} }""" "")).Layouts.Head.Slots.Head
    Assert.Equal(MonitorRef.Named name, slot.Monitor)

[<Fact>]
let ``a monitor given as a numeric string is an index`` () =
    let slot = (Config.parse (withLayout """{ "app": "Spotify", "monitor": "3" }""" "")).Layouts.Head.Slots.Head
    Assert.Equal(MonitorRef.Index 3, slot.Monitor)

[<Theory>]
[<InlineData("""{ "app": "Slack", "monitor": 1 }""", "no app is named 'Slack' - the launcher defines Spotify")>]
[<InlineData("""{ "monitor": 1 }""", "which app?")>]
[<InlineData("""{ "app": "Spotify" }""", "which monitor?")>]
[<InlineData("""{ "app": "Spotify", "monitor": 0 }""", "monitors are numbered from 1")>]
[<InlineData("""{ "app": "Spotify", "monitor": "0" }""", "monitors are numbered from 1")>]
[<InlineData("""{ "app": "Spotify", "monitor": 1.5 }""", "must be a whole number")>]
[<InlineData("""{ "app": "Spotify", "monitor": " " }""", "no monitor given")>]
[<InlineData("""{ "app": "Spotify", "monitor": true }""", "must be a number or a string")>]
[<InlineData("""{ "app": "Spotify", "monitor": 1, "rect": "1/2, 0, 2/3, 1" }""", "runs off the right")>]
[<InlineData("""{ "app": "Spotify", "monitor": 1, "rect": "0, 1/2, 1, 2/3" }""", "runs off the bottom")>]
[<InlineData("""{ "app": "Spotify", "monitor": 1, "rect": "0, 0, 1" }""", "it has 3 parts")>]
[<InlineData("""{ "app": "Spotify", "monitor": 1, "rect": "0, 0, 0, 1" }""", "width: 0/1 is not usable")>]
[<InlineData("""{ "app": "Spotify", "monitor": 1, "instances": "some" }""", "'some' is not an instances setting")>]
let ``a bad window slot says what is wrong`` (window: string, fragment: string) =
    withLayout window "" |> failsWith fragment

[<Fact>]
let ``a layout with no windows is refused`` () =
    (withLayout "" "") |> failsWith "a layout with no windows in it does nothing"

[<Fact>]
let ``layouts with no key to apply them are refused`` () =
    (withLayout """{ "app": "Spotify", "monitor": 1 }""" "").Replace("\"applyLayout\": \"0\"", "\"applyLayout\": \"none\"")
    |> failsWith "there are layouts, but no key to apply them"

[<Fact>]
let ``a key to apply layouts needs layouts`` () =
    config "\"Ctrl+K\"" "\"Ctrl+Shift+Alt\"" leftZone """, "applyLayout": "0" """
    |> failsWith "no layouts for it to apply"

[<Fact>]
let ``applying on change needs layouts`` () =
    config "\"Ctrl+K\"" "\"Ctrl+Shift+Alt\"" leftZone """, "applyLayoutOnChange": 3 """
    |> failsWith "no layouts to apply"

[<Theory>]
[<InlineData("3", 3.0)>]
[<InlineData("\"2.5\"", 2.5)>]
[<InlineData("\"off\"", 0.0)>]
[<InlineData("\"\"", 0.0)>]
[<InlineData("false", 0.0)>]
[<InlineData("null", 0.0)>]
let ``applying on change takes seconds or off`` (given: string, seconds: float) =
    let settle = (Config.parse (withLayout """{ "app": "Spotify", "monitor": 1 }""" $", \"applyLayoutOnChange\": {given}")).Placement.ApplyLayoutOnChange
    let expected = if seconds = 0.0 then None else Some(TimeSpan.FromSeconds seconds)
    Assert.Equal(expected, settle)

[<Theory>]
[<InlineData("120", "outside the usable range")>]
[<InlineData("0.5", "outside the usable range")>]
[<InlineData("\"soon\"", "'soon' is not a number of seconds")>]
[<InlineData("true", "must be a number of seconds")>]
let ``a bad settle time says what is wrong`` (given: string, fragment: string) =
    withLayout """{ "app": "Spotify", "monitor": 1 }""" $", \"applyLayoutOnChange\": {given}" |> failsWith fragment

[<Fact>]
let ``conditions are read when given`` () =
    let json =
        (withLayout """{ "app": "Spotify", "monitor": 1 }""" "")
            .Replace("\"name\": \"Desk\",", "\"name\": \"Desk\", \"when\": { \"dock\": \" WD25 \", \"monitors\": 3 },")

    Assert.Equal({ Dock = Some "WD25"; Monitors = Some 3 }, (Config.parse json).Layouts.Head.When)

[<Fact>]
let ``a monitor count of zero is refused`` () =
    (withLayout """{ "app": "Spotify", "monitor": 1 }""" "")
        .Replace("\"name\": \"Desk\",", "\"name\": \"Desk\", \"when\": { \"monitors\": 0 },")
    |> failsWith "starts at 1"

[<Fact>]
let ``an unnamed layout is named by its position`` () =
    let json = (withLayout """{ "app": "Spotify", "monitor": 1 }""" "").Replace("\"name\": \"Desk\",", "")
    Assert.Equal("layout 1", (Config.parse json).Layouts.Head.Name)
