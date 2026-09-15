// 4 of 12 - reading mashedpotato.json
//
// The only place that knows the file format. Turns JSON into the Domain types, and
// holds the settings currently in force.
//
// There are no defaults in the code: if the configuration cannot be read, nothing is
// bound, which is loud and deliberate. It is read from
// %APPDATA%\Mashed Potato\mashedpotato.json, and on a first run the copy shipped beside the
// executable is installed there to be edited - a deployment default, not a code one.
//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

module MashedPotato.Config

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Windows.Forms

module Dto =

    [<CLIMutable>]
    type App =
        { key : string
          name : string
          processName : string
          paths : string array
          shellFallback : string }

    [<CLIMutable>]
    type Zone =
        { key : string
          name : string
          side : string
          width : string
          height : string }

    /// prefix is left as a JsonElement because it has two legal shapes - a string
    /// or an object - and System.Text.Json cannot bind both to one typed field.
    [<CLIMutable>]
    type Launcher =
        { prefix : JsonElement
          passThroughIn : string array
          apps : App array }

    [<CLIMutable>]
    type Placement =
        { prefix : JsonElement
          nextMonitor : string
          cycleDisplay : string
          applyLayout : string
          applyLayoutOnChange : JsonElement
          zones : Zone array }

    /// monitor is a JsonElement for the same reason prefix is: it is legally either
    /// a number (1) or a string ("primary", "Dell P2222H").
    [<CLIMutable>]
    type Slot =
        { app : string
          monitor : JsonElement
          rect : string
          instances : string }

    /// Nullable rather than int, so "not given" is distinguishable from 0 - which
    /// would otherwise mean "matches only when no monitors are attached".
    [<CLIMutable>]
    type Conditions =
        { dock : string
          monitors : Nullable<int> }

    /// `when` is an F# keyword, hence the backticks. The JSON property is plain.
    [<CLIMutable>]
    type Profile =
        { name : string
          ``when`` : Conditions
          windows : Slot array }

    [<CLIMutable>]
    type Root =
        { launcher : Launcher
          placement : Placement
          layouts : Profile array }

let private reading =
    JsonSerializerOptions(
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true)

/// A key can be written either as the character it types - "[", ";", "4" - or as
/// a name from the Keys enum - "F1", "Escape", "NumPad7". The character form is
/// resolved through the keyboard layout, so it means "the key in that position on
/// this keyboard" rather than a fixed code, and saves anyone having to know that
/// "[" is spelled OemOpenBrackets.
let private keyCode context (text: string) =
    if String.IsNullOrWhiteSpace text then
        failwith $"{context}: no key given"
    else
        let trimmed = text.Trim()

        if trimmed.Length = 1 then
            // VkKeyScan's high byte is the shift state needed to type the
            // character; only the key itself matters here, so "[" and "{" name
            // the same physical key.
            match int (Interop.VkKeyScan trimmed.[0]) with
            | -1 -> failwith $"{context}: '{trimmed}' cannot be typed on the current keyboard layout"
            | scanned -> scanned &&& 0xFF
        else
            // Enum.TryParse also accepts the numbers *behind* enum names, so "1"
            // would quietly become Keys.LButton. No Keys name starts with a digit.
            match Enum.TryParse<Keys>(trimmed, true) with
            | true, parsed when not (Char.IsDigit trimmed.[0]) -> int parsed
            | _ ->
                failwith $"{context}: '{trimmed}' is not a key - use the character it types, or a Keys name such as F1, Escape, NumPad7, Space"

let private modifier (text: string) =
    match text.Trim().ToLowerInvariant() with
    | "ctrl" | "control" -> Some Modifier.Ctrl
    | "shift" -> Some Modifier.Shift
    | "alt" | "menu" -> Some Modifier.Alt
    | "win" | "windows" | "super" -> Some Modifier.Win
    | _ -> None

/// Both spellings of a prefix reduce to the same list of tokens:
///   "Ctrl+K"                        -> [ "Ctrl"; "K" ]
///   { "key1": "Ctrl", "key2": "K" } -> [ "Ctrl"; "K" ]
/// The object form is ordered by the number in each property name, so key10 sorts
/// after key9 rather than after key1.
let private prefixTokens context (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.String ->
        element.GetString().Split('+')
        |> Array.map (fun part -> part.Trim())
        |> Array.filter (String.IsNullOrEmpty >> not)
        |> List.ofArray

    | JsonValueKind.Object ->
        let order (name: string) =
            match Int32.TryParse(String(name |> Seq.filter Char.IsDigit |> Seq.toArray)) with
            | true, number -> number
            | _ -> Int32.MaxValue

        element.EnumerateObject()
        |> Seq.sortBy (fun property -> order property.Name)
        |> Seq.map (fun property -> property.Value.GetString())
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> List.ofSeq

    | JsonValueKind.Undefined ->
        failwith $"{context}: a prefix is required"

    | other ->
        failwith $"{context}: a prefix must be a string such as Ctrl+K, or an object of key1/key2 entries - not {other}"

/// `wantsKey` says which shape this section needs: a chord prefix ends in a key,
/// a held prefix is modifiers alone. Getting it wrong is a config error rather
/// than something to guess at.
let private prefix context wantsKey element =
    let tokens = prefixTokens context element
    let modifiers = tokens |> List.choose modifier |> Set.ofList
    let rest = tokens |> List.filter (fun token -> (modifier token).IsNone)

    if Set.isEmpty modifiers then
        failwith $"{context}: a prefix needs at least one modifier (Ctrl, Shift, Alt, Win)"

    match wantsKey, rest with
    | true, [ only ] -> { Modifiers = modifiers; Key = Some(keyCode context only) }
    | true, [] -> failwith $"{context}: this prefix needs a key as well as modifiers, as in Ctrl+K"
    | true, _ -> failwith $"{context}: this prefix has more than one non-modifier key"
    | false, [] -> { Modifiers = modifiers; Key = None }
    | false, extra ->
        let listed = String.Join(", ", extra)
        failwith $"{context}: this prefix is held down, so it must be modifiers only - remove {listed}"

let private anchors context (text: string) =
    let letters =
        if isNull text then ""
        else String(text |> Seq.filter Char.IsLetter |> Seq.toArray).ToLowerInvariant()

    match letters with
    | "left" -> Anchor.Start, Anchor.Middle
    | "right" -> Anchor.End, Anchor.Middle
    | "top" -> Anchor.Middle, Anchor.Start
    | "bottom" -> Anchor.Middle, Anchor.End
    | "topleft" | "lefttop" -> Anchor.Start, Anchor.Start
    | "topright" | "righttop" -> Anchor.End, Anchor.Start
    | "bottomleft" | "leftbottom" -> Anchor.Start, Anchor.End
    | "bottomright" | "rightbottom" -> Anchor.End, Anchor.End
    | "center" | "centre" | "middle" -> Anchor.Middle, Anchor.Middle
    | _ ->
        failwith $"{context}: '{text}' is not a side - use left, right, top, bottom, topleft, topright, bottomleft, bottomright or center"

/// "2/3", or a bare "1" for the whole axis. `allowZero` is for the left and top
/// edges of a layout rect, which legitimately start at 0; a zone's width never can.
let private fractionOf context allowZero (text: string) =
    let number (part: string) =
        match Int32.TryParse(part.Trim()) with
        | true, value -> value
        | _ -> failwith $"{context}: '{text}' is not a fraction - use 1, 1/2, 2/3"

    let parsed =
        match text.Split('/') with
        | [| whole |] -> { Numerator = number whole; Denominator = 1 }
        | [| top; bottom |] -> { Numerator = number top; Denominator = number bottom }
        | _ -> failwith $"{context}: '{text}' is not a fraction - use 1, 1/2, 2/3"

    let floor = if allowZero then 0 else 1

    if parsed.Numerator < floor || parsed.Denominator <= 0 || parsed.Numerator > parsed.Denominator then
        let lowest = if allowZero then "at or above zero" else "above zero"
        failwith $"{context}: {parsed.Numerator}/{parsed.Denominator} is not usable - it must be {lowest} and no larger than the monitor"

    parsed

let private fraction context text = fractionOf context false text

let private orEmpty (items: 'a array) = if isNull (box items) then [||] else items

/// Process.GetProcessesByName wants the image name *without* its extension, so
/// "Code.exe" finds nothing at all. Writing it that way is an easy mistake and a
/// silent one, so take the suffix off rather than let it fail later.
let private imageName context (text: string) =
    let trimmed = if isNull text then "" else text.Trim()

    if String.IsNullOrEmpty trimmed then
        failwith $"{context}: processName is required - it is the image name shown in Task Manager's Details tab, without .exe"
    elif trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) then
        trimmed.Substring(0, trimmed.Length - 4)
    else
        trimmed

/// "left, top, width, height", each a fraction: "0, 0, 2/3, 1" is the left two
/// thirds, full height. Four numbers rather than a side-and-size like a zone,
/// because a layout has to be able to say "from a third across to a half" - which
/// no anchor can express.
let private rect context (text: string) =
    if String.IsNullOrWhiteSpace text then
        failwith $"{context}: a rect is required - four fractions, as in \"0, 0, 2/3, 1\" (left, top, width, height)"

    match text.Split(',') with
    | [| left; top; width; height |] ->
        let parsed =
            { Left = fractionOf $"{context} left" true left
              Top = fractionOf $"{context} top" true top
              Width = fractionOf $"{context} width" false width
              Height = fractionOf $"{context} height" false height }

        // Cross-multiplied rather than divided, so this stays exact: a/b + c/d <= 1
        // is ad + cb <= bd. int64 because two denominators multiply.
        let fits (start: Fraction) (size: Fraction) =
            int64 start.Numerator * int64 size.Denominator
            + int64 size.Numerator * int64 start.Denominator
            <= int64 start.Denominator * int64 size.Denominator

        if not (fits parsed.Left parsed.Width) then
            failwith $"{context}: left {left.Trim()} plus width {width.Trim()} runs off the right of the monitor"

        if not (fits parsed.Top parsed.Height) then
            failwith $"{context}: top {top.Trim()} plus height {height.Trim()} runs off the bottom of the monitor"

        parsed

    | parts ->
        failwith $"{context}: '{text}' is not a rect - it has {parts.Length} parts, and a rect has four: left, top, width, height, as in \"0, 0, 2/3, 1\""

/// Seconds to wait for the hardware to settle, or "off". A number rather than a
/// flag because the wait is the part that varies by machine: monitors coming up
/// through a dock take as long as they take, and applying a layout to a desktop
/// that is still rearranging itself achieves nothing.
let private settle context (element: JsonElement) =
    let seconds value =
        if value < 1.0 || value > 60.0 then
            failwith $"{context}: {value} seconds is outside the usable range - use 1 to 60, or \"off\""
        Some(TimeSpan.FromSeconds value)

    match element.ValueKind with
    | JsonValueKind.Undefined | JsonValueKind.Null -> None
    | JsonValueKind.False -> None
    | JsonValueKind.Number -> seconds (element.GetDouble())
    | JsonValueKind.String ->
        match element.GetString() with
        | null -> None
        | text when String.IsNullOrWhiteSpace text -> None
        | text when text.Trim().Equals("off", StringComparison.OrdinalIgnoreCase) -> None
        | text ->
            match Double.TryParse(text.Trim()) with
            | true, value -> seconds value
            | _ -> failwith $"{context}: '{text}' is not a number of seconds - use a number from 1 to 60, or \"off\""
    | other ->
        failwith $"{context}: must be a number of seconds or \"off\" - not {other}"

/// "front" (the default) or "all".
let private instances context (text: string) =
    match (if isNull text then "" else text.Trim().ToLowerInvariant()) with
    | "" | "front" | "one" -> Instances.Front
    | "all" | "every" -> Instances.All
    | other ->
        failwith $"{context}: '{other}' is not an instances setting - use \"all\" for every window the app has open, or leave it out for just the front one"

/// A number from 1, "primary", or part of a monitor's name.
let private monitorRef context (element: JsonElement) =
    let fromText (text: string) =
        if text.Equals("primary", StringComparison.OrdinalIgnoreCase) then
            MonitorRef.Primary
        else
            match Int32.TryParse text with
            | true, number when number >= 1 -> MonitorRef.Index number
            | true, _ -> failwith $"{context}: monitors are numbered from 1"
            | _ -> MonitorRef.Named text

    match element.ValueKind with
    | JsonValueKind.Number ->
        match element.TryGetInt32() with
        | true, number when number >= 1 -> MonitorRef.Index number
        | true, _ -> failwith $"{context}: monitors are numbered from 1"
        | _ -> failwith $"{context}: a monitor number must be a whole number"

    | JsonValueKind.String ->
        match element.GetString() with
        | null -> failwith $"{context}: no monitor given"
        | text when String.IsNullOrWhiteSpace text -> failwith $"{context}: no monitor given"
        | text -> fromText (text.Trim())

    | JsonValueKind.Undefined ->
        failwith $"{context}: which monitor? a number from 1, \"primary\", or part of a monitor's name (the tray menu lists them)"

    | other ->
        failwith $"{context}: a monitor must be a number or a string - not {other}"

let private interpret (root: Dto.Root) =
    if obj.ReferenceEquals(root.launcher, null) then failwith "a launcher section is required"
    if obj.ReferenceEquals(root.placement, null) then failwith "a placement section is required"

    let apps =
        orEmpty root.launcher.apps
        |> Array.map (fun app ->
            keyCode $"app '{app.name}'" app.key,
            { Name = app.name
              ProcessName = imageName $"app '{app.name}'" app.processName

              // ExpandEnvironmentVariables turns "%APPDATA%\Spotify\..." into a
              // real path. It is the same expansion the shell does, so the JSON
              // can be written the way a person would type it.
              ExePaths =
                orEmpty app.paths
                |> Array.map Environment.ExpandEnvironmentVariables
                |> List.ofArray
              ShellFallback = app.shellFallback })
        |> List.ofArray

    // A layout names apps rather than redefining them, so the two sections cannot
    // drift apart - and a typo is caught here rather than silently placing nothing.
    let appNamed context (name: string) =
        let wanted = if isNull name then "" else name.Trim()

        if wanted = "" then
            failwith $"{context}: which app? give the name of one from the launcher's apps"

        match apps |> List.tryFind (fun (_, target) -> String.Equals(target.Name, wanted, StringComparison.OrdinalIgnoreCase)) with
        | Some(_, target) -> target
        | None ->
            let known = String.Join(", ", apps |> List.map (fun (_, target) -> target.Name) |> List.distinct)
            failwith $"{context}: no app is named '{wanted}' - the launcher defines {known}"

    let layouts =
        orEmpty root.layouts
        |> Array.mapi (fun index profile ->
            let name = if String.IsNullOrWhiteSpace profile.name then $"layout {index + 1}" else profile.name.Trim()
            let where = $"layout '{name}'"

            let conditions =
                if obj.ReferenceEquals(profile.``when``, null) then
                    { Dock = None; Monitors = None }
                else
                    { Dock =
                        match profile.``when``.dock with
                        | null -> None
                        | text when String.IsNullOrWhiteSpace text -> None
                        | text -> Some(text.Trim())

                      Monitors =
                        if profile.``when``.monitors.HasValue then
                            if profile.``when``.monitors.Value < 1 then
                                failwith $"{where}: 'monitors' counts attached monitors, so it starts at 1"
                            Some profile.``when``.monitors.Value
                        else
                            None }

            let slots =
                orEmpty profile.windows
                |> Array.map (fun slot ->
                    let target = appNamed $"{where}" slot.app
                    { Target = target
                      Monitor = monitorRef $"{where}, {target.Name}" slot.monitor

                      // Omitted on purpose is the interesting case: it means "that
                      // monitor, and I do not care where on it", which is the only
                      // way to say so - every rect decides a size.
                      Rect =
                        if String.IsNullOrWhiteSpace slot.rect then None
                        else Some(rect $"{where}, {target.Name}" slot.rect)

                      Instances = instances $"{where}, {target.Name}" slot.instances })
                |> List.ofArray

            if List.isEmpty slots then
                failwith $"{where}: a layout with no windows in it does nothing - give it a 'windows' list"

            { Name = name; When = conditions; Slots = slots })
        |> List.ofArray

    let applyLayoutVk =
        match (if isNull root.placement.applyLayout then "" else root.placement.applyLayout.Trim()) with
        | "" | "none" -> None
        | given -> Some(keyCode "placement applyLayout" given)

    let applyLayoutOnChange = settle "placement applyLayoutOnChange" root.placement.applyLayoutOnChange

    // The two halves of the feature live in different sections, so each is checked
    // against the other: a key bound to nothing, or layouts nothing can reach, are
    // both mistakes that would otherwise just quietly not work.
    if applyLayoutVk.IsSome && List.isEmpty layouts then
        failwith "placement applyLayout names a key, but there are no layouts for it to apply - add a layouts section"

    if applyLayoutVk.IsNone && not (List.isEmpty layouts) then
        failwith "there are layouts, but no key to apply them - add applyLayout to the placement section, as in \"applyLayout\": \"0\""

    if applyLayoutOnChange.IsSome && List.isEmpty layouts then
        failwith "placement applyLayoutOnChange asks for a layout when the hardware changes, but there are no layouts to apply - add a layouts section"

    { Launcher =
        { Prefix = prefix "launcher prefix" true root.launcher.prefix
          PassThroughIn = orEmpty root.launcher.passThroughIn
          Apps = apps }

      Placement =
        { Prefix = prefix "placement prefix" false root.placement.prefix

          NextMonitorVk =
            match (if isNull root.placement.nextMonitor then "" else root.placement.nextMonitor.Trim()) with
            | "" | "none" -> None
            | given -> Some(keyCode "placement nextMonitor" given)

          CycleDisplayVk =
            match (if isNull root.placement.cycleDisplay then "" else root.placement.cycleDisplay.Trim()) with
            | "" | "none" -> None
            | given -> Some(keyCode "placement cycleDisplay" given)

          ApplyLayoutVk = applyLayoutVk
          ApplyLayoutOnChange = applyLayoutOnChange

          Zones =
            orEmpty root.placement.zones
            |> Array.map (fun zone ->
                let where = $"zone '{zone.name}'"
                let horizontal, vertical = anchors where zone.side

                // An omitted fraction means the whole axis, which is what makes
                // "height" optional for the common full-height zones.
                let axis name (given: string) =
                    if String.IsNullOrWhiteSpace given then { Numerator = 1; Denominator = 1 }
                    else fraction $"{where} {name}" given

                { Key = keyCode where zone.key
                  Name = zone.name
                  Horizontal = horizontal
                  Vertical = vertical
                  Width = axis "width" zone.width
                  Height = axis "height" zone.height })
            |> List.ofArray }

      Layouts = layouts }

/// Environment.SpecialFolder is the portable way to name the folders Windows moves
/// around between versions - never hard-code C:\Users\... ApplicationData is
/// %APPDATA%, which roams between machines on a domain account; LocalApplicationData
/// (%LOCALAPPDATA%) is the one that does not. Settings roam, caches do not.
let file =
    Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.ApplicationData,
        "Mashed Potato",
        "mashedpotato.json")

/// The copy that ships next to the executable, installed on a first run.
let private shipped = Path.Combine(AppContext.BaseDirectory, "mashedpotato.json")

/// Whether the file can be read *right now*. A save arrives as an event before the
/// editor has necessarily let go of the file, and a read in that window either
/// fails or returns half a file - neither of which is a broken configuration, and
/// neither of which should be reported as one.
let readable (path: string) =
    try
        use handle = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        handle.Length >= 0L
    with _ ->
        false

/// Calls back whenever the file changes. Returns the watcher to dispose, or None if
/// one could not be set up - which is not worth failing to start over, since the
/// tray's "Reload config" still works.
///
/// Created and Renamed matter as much as Changed, and this is the part that is easy
/// to get wrong. A great many editors - VS Code, vim, Notepad since Windows 10 - do
/// not write to the file at all: they write a temporary one beside it and rename it
/// over the top, which is what makes a save atomic, and what makes a watcher
/// listening only for Changed miss the save completely.
///
/// Deleted is deliberately not watched. It is mostly the first half of one of those
/// renames, and reacting to it would mean reading a file that is about to exist
/// again - or worse, `load` reinstalling the shipped copy over an edit in progress.
///
/// The callback arrives on a thread pool thread, as all FileSystemWatcher events do.
let watch (path: string) (onChanged: unit -> unit) =
    try
        let watcher =
            new FileSystemWatcher(
                Path = Path.GetDirectoryName path,
                Filter = Path.GetFileName path,
                NotifyFilter = (NotifyFilters.LastWrite ||| NotifyFilters.FileName ||| NotifyFilters.Size))

        watcher.Changed.Add(fun _ -> onChanged ())
        watcher.Created.Add(fun _ -> onChanged ())
        watcher.Renamed.Add(fun _ -> onChanged ())
        watcher.EnableRaisingEvents <- true
        Some watcher
    with error ->
        Log.write "watch config" error
        None

/// None until a configuration has been read successfully. Nothing is bound in that
/// state - there is deliberately nothing to fall back to. Replaced wholesale on
/// reload: the hook reads this on every keystroke from its own callback while the
/// UI thread may be swapping it, and a reference assignment is atomic, so the worst
/// a keystroke can see is the settings from a moment ago.
let mutable current : Settings option = None

/// Returns a message when the file could not be used. A failed *reload* leaves the
/// previous settings in force rather than unbinding everything mid-session.
let load () =
    try
        if not (File.Exists file) then
            if File.Exists shipped then
                Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
                File.Copy(shipped, file)
                Log.note "config" $"Installed a starting configuration at {file}."
            else
                failwith $"no configuration at {file}, and no mashedpotato.json beside the executable to install from"

        let root = JsonSerializer.Deserialize<Dto.Root>(File.ReadAllText file, reading)
        if obj.ReferenceEquals(root, null) then failwith "the file is empty"

        current <- Some(interpret root)
        None
    with error ->
        Log.write "config" error

        let consequence =
            if current.IsSome then "The previous configuration is still in force."
            else "Nothing is bound until this is fixed."

        Some $"{error.Message}\n\n{consequence}"
