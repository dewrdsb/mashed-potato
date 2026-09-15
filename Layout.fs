// 8 of 12 - putting every window where it belongs
//
// One action: read the layout that fits what is plugged in, and move each window
// named by it. The only file that knows a desktop can be arranged as a whole rather
// than one window at a time.
//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

module MashedPotato.Layout

open System
open System.Drawing
open System.Threading
open System.Windows.Forms

open Interop

// WHY A LAYOUT NEEDS PROFILES AT ALL
//
// The same three windows want three different arrangements depending on what the
// machine is plugged into, and the machine cannot be asked directly: there is no
// "which desk am I at" call. What there is, is the set of monitors attached and the
// set of devices present, and between them those identify a desk well enough.
//
// Monitor *count* alone does not, which is the trap worth knowing about. A laptop
// docked to two externals with the lid shut has two monitors; the same laptop at
// home with its lid open and one external also has two. The dock is what separates
// them, so a profile can ask about both.

/// Set by the host so failures can surface as a tray balloon.
let mutable onError : string -> unit = ignore

/// A monitor as the configuration talks about it: the number written in a profile,
/// the name matched against, and the area windows go in.
type Monitor =
    { Number : int
      Name : string
      Primary : bool
      Area : Rectangle }

    member this.Description =
        let primary = if this.Primary then " (primary)" else ""
        $"{this.Number}: {this.Name} {this.Area.Width}x{this.Area.Height}{primary}"

/// Numbered the way Snap numbers them - by adapter name, which is the numbering
/// Display Settings shows - so "monitor 2" means the same thing to both features.
/// The friendly name comes off the EDID and is what a profile can match on.
let private monitors () =
    Snap.orderedScreens ()
    |> Array.mapi (fun index screen ->
        { Number = index + 1
          Name = monitorNameOf screen.DeviceName |> Option.defaultValue screen.DeviceName
          Primary = screen.Primary
          Area = screen.WorkingArea })

let private contains (needle: string) (haystack: string) =
    haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

let private resolve reference (screens: Monitor array) =
    match reference with
    | MonitorRef.Index number -> screens |> Array.tryFind (fun screen -> screen.Number = number)
    | MonitorRef.Primary -> screens |> Array.tryFind (fun screen -> screen.Primary)
    | MonitorRef.Named name -> screens |> Array.tryFind (fun screen -> contains name screen.Name)

let private describeRef reference =
    match reference with
    | MonitorRef.Index number -> $"monitor {number}"
    | MonitorRef.Primary -> "the primary monitor"
    | MonitorRef.Named name -> $"a monitor named '{name}'"

/// Where a slot's rect lands on a monitor. Built from Snap.span, so a layout and a
/// snap round a fraction the same way and windows meant to meet actually do.
let private target (rect: FractionRect) (area: Rectangle) =
    Rectangle(
        area.Left + Snap.span rect.Left area.Width,
        area.Top + Snap.span rect.Top area.Height,
        Snap.span rect.Width area.Width,
        Snap.span rect.Height area.Height)

/// What is plugged in, gathered once. The device list is `lazy` because walking the
/// device tree costs a good fraction of a second and most configurations never ask
/// about a dock - forcing it only when a profile mentions one keeps the common case
/// as fast as reading the monitors.
let private look () =
    monitors (), lazy (presentDeviceNames ())

/// Every monitor the profile names is actually attached. This is a condition
/// nobody writes, because a profile that cannot be carried out should never be the
/// one chosen - and asking directly is better than the count it replaces.
///
/// Counting was the first attempt and it is subtly wrong. Two externals with the
/// lid shut is two monitors; open the lid and it is three, and a profile pinned to
/// `"monitors": 2` silently stops matching the desk it was written for. What the
/// office layout actually needs is not "two monitors" but "monitors 1 and 2 exist",
/// which stays true either way.
let private reachable (screens: Monitor array) (profile: LayoutProfile) =
    profile.Slots |> List.forall (fun slot -> (resolve slot.Monitor screens).IsSome)

let private matching (profiles: LayoutProfile list) (screens: Monitor array) (devices: Lazy<string list>) =
    let docked wanted = devices.Value |> List.exists (contains wanted)

    profiles
    |> List.tryFind (fun profile ->
        // Cheap tests first: the dock is a walk of the whole device tree, and the
        // `lazy` around it means an early `false` here avoids paying for it at all.
        (match profile.When.Monitors with
         | Some count -> count = screens.Length
         | None -> true)
        && reachable screens profile
        && (match profile.When.Dock with
            | Some wanted -> docked wanted
            | None -> true))

/// What the tray menu shows and the log records: the monitors, any docks that are
/// present, and which profile that adds up to. Deliberately the same matching
/// `apply` does, so the menu cannot claim one thing and the keystroke do another.
let survey () =
    let screens, devices = look ()
    let profiles = match Config.current with Some settings -> settings.Layouts | None -> []

    // Only ever displayed, never matched on: matching uses whatever string the
    // profile asked for. This is the list to read when filling in a new profile.
    let docks =
        if List.isEmpty profiles then []
        else devices.Value |> List.filter (contains "dock") |> List.distinct

    screens |> Array.map (fun screen -> screen.Description) |> List.ofArray,
    docks,
    matching profiles screens devices

/// Put one window where its slot says. Two quite different jobs, and which one is
/// wanted is decided by whether the slot gave a rect:
///
/// * a rect is an exact demand, so the window is restored first - it cannot be
///   "exactly there" while minimized, and a minimized window silently taking the
///   geometry looks precisely like the key having done nothing;
///
/// * no rect asks only about which monitor, so a minimized window keeps its state
///   and has its restore position moved instead. It ends up belonging to the right
///   monitor without anything appearing on screen, which is what "I do not care
///   about their position" ought to mean.
let private put (slot: Slot) (monitor: Monitor) hwnd =
    match slot.Rect with
    | Some rect ->
        if IsIconic(hwnd) then ShowWindow(hwnd, SW_RESTORE) |> ignore
        Snap.place hwnd (target rect monitor.Area)

    | None ->
        Snap.toMonitor hwnd monitor.Area

/// What an event-driven pass concluded. Returned rather than logged from in here,
/// so the caller decides how loud to be - and so it can be tested without a dock to
/// plug and unplug.
[<RequireQualifiedAccess>]
type Outcome =
    | Applied of string
    | Unchanged of string
    | NoMatch

/// The profile believed to be in force. This is the whole of the event story: the
/// broadcasts that say the hardware changed cannot say *what* changed, and most of
/// them are nothing to do with docks at all - a USB stick raises the same message a
/// dock does. Recomputing the answer and comparing it with this is what turns a
/// stream of meaningless notifications into "the desk changed".
let mutable private inForce : string option = None

/// Two of these must not overlap: the keystroke runs on the message loop and the
/// event-driven pass runs on the thread pool, and a dock arriving while the key is
/// pressed is exactly the sort of coincidence that only shows up in the field.
let mutable private running = 0

let private windowsOf (slot: Slot) =
    match slot.Instances with
    | Instances.All -> App.windowsFor slot.Target
    | Instances.Front -> App.windowOf slot.Target |> Option.toList

/// Move every window a profile names, and say what could not be done. Apps that are
/// not running are skipped rather than launched: a launch would have to be waited on
/// before its window could be placed, and a layout that sometimes takes ten seconds
/// is worse than one that says what it could not do.
let private carryOut (screens: Monitor array) (profile: LayoutProfile) =
    let skipped = ResizeArray<string>()

    for slot in profile.Slots do
        match resolve slot.Monitor screens with
        | None -> skipped.Add $"{slot.Target.Name}: {describeRef slot.Monitor} is not attached"
        | Some monitor ->
            match windowsOf slot with
            | [] -> skipped.Add $"{slot.Target.Name} has no window open"
            | windows -> for hwnd in windows do put slot monitor hwnd

    let attached = screens |> Array.map (fun screen -> screen.Description) |> String.concat ", "
    let placed = profile.Slots.Length - skipped.Count
    Log.note "layout" $"Applied '{profile.Name}' to {placed} of {profile.Slots.Length} windows ({attached})."

    if skipped.Count > 0 then
        // Built outside the interpolation: an F# interpolated string cannot carry a
        // string literal inside its own braces.
        let listed = String.Join(", ", skipped)
        onError $"{profile.Name}: {listed}"

/// Take the latch only if it is clear, the way Display.cycle does. Returns what the
/// work produced, or None if another pass already had it.
let private exclusively work =
    if Interlocked.CompareExchange(&running, 1, 0) = 0 then
        try Some(work ()) finally Interlocked.Exchange(&running, 0) |> ignore
    else
        None

/// Remember what is in force without moving anything. Called once the configuration
/// is read, so that the first *change* is what triggers the first rearrangement -
/// rather than the daemon starting up and immediately tidying a desk nobody asked
/// it to touch.
let syncTo (profile: LayoutProfile option) =
    inForce <- profile |> Option.map (fun p -> p.Name)

/// The keystroke: apply whatever fits, whether or not it is already in force. This
/// one is unconditional on purpose - it is what you press when something has moved
/// and you want it back, and "nothing has changed" is not an answer to that.
let apply () =
    try
        match Config.current with
        | None -> ()
        | Some settings when List.isEmpty settings.Layouts -> ()
        | Some settings ->
            exclusively (fun () ->
                let screens, devices = look ()

                match matching settings.Layouts screens devices with
                | None ->
                    inForce <- None
                    let attached = screens |> Array.map (fun screen -> screen.Description) |> String.concat ", "
                    Log.note "layout" $"No layout matches what is attached ({attached})."
                    onError $"No layout fits what is plugged in.\n\nAttached: {attached}"

                | Some profile ->
                    inForce <- Some profile.Name
                    carryOut screens profile)
            |> ignore
    with error ->
        Log.write "layout" error

/// The event path: work out what fits now, and place windows only if that is not
/// what was already in force.
///
/// The comparison is the whole point. These passes are driven by broadcasts that
/// fire for every USB device on the machine, and rearranging someone's windows
/// because they plugged in a phone would be indefensible.
let applyIfChanged () =
    try
        match Config.current with
        | None -> Outcome.NoMatch
        | Some settings when List.isEmpty settings.Layouts -> Outcome.NoMatch
        | Some settings ->
            let decide () =
                let screens, devices = look ()

                match matching settings.Layouts screens devices with
                | None ->
                    inForce <- None
                    Outcome.NoMatch

                | Some profile when inForce = Some profile.Name ->
                    Outcome.Unchanged profile.Name

                | Some profile ->
                    inForce <- Some profile.Name
                    carryOut screens profile
                    Outcome.Applied profile.Name

            // Busy means another pass is already doing this; saying "unchanged" is
            // both true enough and the quiet answer, which is what a notification
            // nobody asked for deserves.
            exclusively decide |> Option.defaultValue (Outcome.Unchanged "busy")
    with error ->
        Log.write "layout" error
        Outcome.NoMatch
