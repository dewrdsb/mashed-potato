// Choosing a layout profile for what is plugged in, and where a slot's rect lands.

module MashedPotato.Tests.LayoutTests

open System.Drawing
open Xunit

open MashedPotato
open MashedPotato.Layout

let private screen number name primary =
    { Number = number; Name = name; Primary = primary; Area = Rectangle((number - 1) * 1920, 0, 1920, 1040) }

let private laptop = screen 1 "Built-in Display" false
let private dell = screen 2 "Dell P2222H" true

let private spotify =
    { Name = "Spotify"; ProcessName = "Spotify"; ExePaths = []; ShellFallback = ""; Tab = None }

let private slot monitor =
    { Target = spotify; Monitor = monitor; Rect = None; Instances = Instances.Front }

let private profile name dock monitors slots =
    { Name = name; When = { Dock = dock; Monitors = monitors }; Slots = slots }

/// The device list, for profiles that ask about a dock.
let private devices names = lazy names

/// A device list that fails the test if anything looks at it.
let private untouchable = lazy (failwith "the device tree was walked when nothing needed it")

let private chosen profiles screens devices =
    matching profiles screens devices |> Option.map (fun chosen -> chosen.Name)

[<Fact>]
let ``a rect lands on the monitor's working area`` () =
    let rect =
        { Left = { Numerator = 1; Denominator = 3 }
          Top = { Numerator = 0; Denominator = 1 }
          Width = { Numerator = 2; Denominator = 3 }
          Height = { Numerator = 1; Denominator = 2 } }

    Assert.Equal(Rectangle(2560, 0, 1280, 520), Layout.target rect dell.Area)

[<Fact>]
let ``monitors are found by number, primary, or part of their name`` () =
    let screens = [| laptop; dell |]
    Assert.Equal(Some laptop, resolve (MonitorRef.Index 1) screens)
    Assert.Equal(None, resolve (MonitorRef.Index 3) screens)
    Assert.Equal(Some dell, resolve MonitorRef.Primary screens)
    Assert.Equal(Some dell, resolve (MonitorRef.Named "p2222h") screens)
    Assert.Equal(None, resolve (MonitorRef.Named "LG") screens)

[<Fact>]
let ``the first profile that matches wins`` () =
    let profiles =
        [ profile "two" None (Some 2) [ slot (MonitorRef.Index 2) ]
          profile "fallback" None None [ slot (MonitorRef.Index 1) ] ]

    Assert.Equal(Some "two", chosen profiles [| laptop; dell |] untouchable)
    Assert.Equal(Some "fallback", chosen profiles [| laptop |] untouchable)

[<Fact>]
let ``a profile naming a monitor that is not attached is skipped`` () =
    let profiles =
        [ profile "needs the dell" None None [ slot (MonitorRef.Named "Dell") ]
          profile "fallback" None None [ slot MonitorRef.Primary ] ]

    Assert.Equal(Some "fallback", chosen profiles [| { laptop with Primary = true } |] untouchable)

[<Fact>]
let ``nothing matches when no profile fits`` () =
    let profiles = [ profile "three" None (Some 3) [ slot (MonitorRef.Index 1) ] ]
    Assert.Equal(None, chosen profiles [| laptop; dell |] untouchable)

[<Fact>]
let ``a dock is matched by part of a device name, ignoring case`` () =
    let profiles =
        [ profile "office" (Some "wd25") None [ slot (MonitorRef.Index 1) ]
          profile "home" None None [ slot (MonitorRef.Index 1) ] ]

    Assert.Equal(Some "office", chosen profiles [| laptop |] (devices [ "USB Hub"; "Dell Pro Dock WD25" ]))
    Assert.Equal(Some "home", chosen profiles [| laptop |] (devices [ "USB Hub" ]))

[<Fact>]
let ``the device tree is not walked when the cheap tests already fail`` () =
    let profiles =
        [ profile "office" (Some "WD25") (Some 3) [ slot (MonitorRef.Index 1) ]
          profile "unreachable" (Some "WD25") None [ slot (MonitorRef.Index 5) ]
          profile "home" None None [ slot (MonitorRef.Index 1) ] ]

    Assert.Equal(Some "home", chosen profiles [| laptop; dell |] untouchable)
