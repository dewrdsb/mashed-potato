// 4 of 10 - reading mashedpotato.json
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
          zones : Zone array }

    [<CLIMutable>]
    type Root =
        { launcher : Launcher
          placement : Placement }

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

/// "2/3", or a bare "1" for the whole axis.
let private fraction context (text: string) =
    let number (part: string) =
        match Int32.TryParse(part.Trim()) with
        | true, value -> value
        | _ -> failwith $"{context}: '{text}' is not a fraction - use 1, 1/2, 2/3"

    let parsed =
        match text.Split('/') with
        | [| whole |] -> { Numerator = number whole; Denominator = 1 }
        | [| top; bottom |] -> { Numerator = number top; Denominator = number bottom }
        | _ -> failwith $"{context}: '{text}' is not a fraction - use 1, 1/2, 2/3"

    if parsed.Numerator <= 0 || parsed.Denominator <= 0 || parsed.Numerator > parsed.Denominator then
        failwith $"{context}: {parsed.Numerator}/{parsed.Denominator} is not usable - it must be above zero and no larger than the monitor"

    parsed

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

let private interpret (root: Dto.Root) =
    if obj.ReferenceEquals(root.launcher, null) then failwith "a launcher section is required"
    if obj.ReferenceEquals(root.placement, null) then failwith "a placement section is required"

    { Launcher =
        { Prefix = prefix "launcher prefix" true root.launcher.prefix
          PassThroughIn = orEmpty root.launcher.passThroughIn
          Apps =
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
            |> List.ofArray }

      Placement =
        { Prefix = prefix "placement prefix" false root.placement.prefix

          NextMonitorVk =
            match (if isNull root.placement.nextMonitor then "" else root.placement.nextMonitor.Trim()) with
            | "" | "none" -> None
            | given -> Some(keyCode "placement nextMonitor" given)

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
            |> List.ofArray } }

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
