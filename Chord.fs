// 10 of 13 - the keyboard hook
//
// The only part that sees keystrokes. Decides what a key means and hands the work
// to callbacks rather than doing it here.
//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

module MashedPotato.Chord

open System
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open System.Windows.Forms

open Interop

// WHAT A LOW-LEVEL KEYBOARD HOOK IS
//
// SetWindowsHookEx(WH_KEYBOARD_LL, ...) asks Windows to call us for every keystroke
// on the whole desktop, before the focused application sees it. Our callback
// returns either "pass it on" or a non-zero value meaning "swallow this" - the
// keystroke then never reaches the app that would have got it. That is the entire
// mechanism behind this program, and behind AutoHotkey.
//
// Three consequences shape the code below:
//
// * Windows calls the hook *on the thread that installed it*, by posting to that
//   thread's message queue. So that thread needs a running message loop for
//   keystrokes to arrive at all. Here it is a thread of its own - see `install`.
//
// * It must return quickly. Windows gives a low-level hook a deadline (a few
//   hundred milliseconds by default) and silently stops calling one that misses
//   it - the hotkeys would just quietly die. All real work is posted, not done
//   here.
//
// * It is called across a native boundary, so an escaping exception terminates
//   the process rather than reaching .NET's handlers. Hence the try/with wrapper.
//
// Two things a hook cannot do: see keystrokes typed into an elevated (admin)
// window unless it is itself elevated, and see anything at all on the secure
// desktop (the lock screen, UAC prompts).

/// What the hook does once a chord or hotkey resolves. Every one of these posts to
/// the message loop rather than running inside the callback.
type Handlers =
    { Toggle : Target -> unit
      Prompt : bool -> unit
      SnapTo : Zone -> unit
      NextMonitor : unit -> unit
      CycleDisplay : unit -> unit
      ApplyLayout : unit -> unit }

// These are module level - effectively static fields - for a reason that catches
// everyone once. Handing a delegate to a C API passes a raw function pointer, and
// the garbage collector has no idea Windows is holding it: it will happily collect
// a delegate that nothing in managed code still references, and the next keystroke
// then calls into freed memory. Keeping `callback` in a field that lives as long
// as the process is what prevents that.
let mutable private hook = 0n
let mutable private callback : LowLevelKeyboardProc = null

/// Set by the prefix, cleared by whatever resolves the chord. There is no timer:
/// the banner is on screen the whole time this is true, so the state is never
/// invisible to the person typing.
let mutable private armed = false

/// The key whose press the hook last swallowed, and when it last went down. A held
/// key auto-repeats - Windows sends the key down over and over, with nothing in the
/// event to say it is a repeat - so without this, holding K a moment too long after
/// Ctrl+K arrived as a second press of an unbound key and took the banner straight
/// back down. The chord looked as if it had done nothing.
let mutable private swallowedHeld : (int * uint32) option = None

/// Windows' longest auto-repeat delay is one second. A gap longer than that is a
/// fresh press whose release the hook never saw - a lock screen in between, say.
let private repeatWindowMs = 1100u

/// Whether a key going down is the auto-repeat of a press that was swallowed. Event
/// times are milliseconds since boot; unsigned subtraction stays right across the
/// 49-day wrap.
let internal isRepeat swallowedHeld vk (time: uint32) =
    match swallowedHeld with
    | Some(heldVk, since) -> heldVk = vk && time - since < repeatWindowMs
    | None -> false

let private isModifier vk =
    match enum<Keys> vk with
    | Keys.ShiftKey
    | Keys.ControlKey
    | Keys.Menu
    | Keys.LWin
    | Keys.RWin -> true
    | key -> key >= Keys.LShiftKey && key <= Keys.RMenu

/// The watch on a swallowed key, once a key goes up: its own release ends it.
let internal released swallowedHeld vk =
    match swallowedHeld with
    | Some(heldVk, _) when heldVk = vk -> None
    | other -> other

/// The watch on a swallowed key, once a key goes down and the hook has decided what
/// to do with it. Only the most recently pressed key repeats, so any other key
/// going down ends the watch on the last one; modifiers and injected keys are not
/// pressed in that sense and leave it be.
let internal pressed swallowedHeld vk time injected swallowed =
    if injected || isModifier vk then swallowedHeld
    elif swallowed then Some(vk, time)
    else None

/// The modifiers held right now, as a set. Comparing sets for *equality* is what
/// makes prefixes exclusive: a "Ctrl" prefix does not fire while Ctrl+Shift is
/// held, because {Ctrl} <> {Ctrl; Shift}.
let private heldModifiers () =
    Set.ofList
        [ if isHeld Keys.ControlKey then Modifier.Ctrl
          if isHeld Keys.ShiftKey then Modifier.Shift
          if isHeld Keys.Menu then Modifier.Alt
          if isHeld Keys.LWin || isHeld Keys.RWin then Modifier.Win ]

let private isPassThroughApp (names: string array) =
    if Array.isEmpty names then
        false
    else
        match processNameOf (GetForegroundWindow()) with
        | None -> false
        | Some name ->
            names
            |> Array.exists (fun excluded -> String.Equals(excluded, name, StringComparison.OrdinalIgnoreCase))

/// One thing a resolved keystroke asks for. Each maps to a field of Handlers; they
/// are values rather than calls so the decision can be made - and tested - apart
/// from the hook that acts on it.
[<RequireQualifiedAccess>]
type internal Effect =
    | Prompt of visible: bool
    | Toggle of Target
    | SnapTo of Zone
    | NextMonitor
    | CycleDisplay
    | ApplyLayout

/// What a keystroke comes to: whether a chord is open afterwards, whether the key
/// stops here, and what to do about it, in order.
type internal Outcome =
    { Armed : bool
      Swallow : bool
      Effects : Effect list }

/// Every decision the hook makes, with nothing in it that touches Windows. The two
/// questions that do - which modifiers are held, and whether the foreground app is
/// one to stay out of - are passed in as functions, so each is only asked when the
/// answer can change the outcome, the way the hook always asked them.
let internal step
    (settings: Settings option)
    armed
    vk
    injected
    (held: unit -> Set<Modifier>)
    (inPassThroughApp: string array -> bool)
    =
    let passOn = { Armed = armed; Swallow = false; Effects = [] }
    let swallow armed effects = { Armed = armed; Swallow = true; Effects = effects }

    // Ignore synthetic input, and let modifiers alone keep the chord open so that
    // "Ctrl+K, Ctrl+C" behaves the same as "Ctrl+K, C".
    if injected || isModifier vk then
        passOn
    else
        match settings with
        // No configuration read, so nothing here is ours to take.
        | None -> passOn

        | Some settings ->
            let held = held ()
            let launcher = settings.Launcher
            let placement = settings.Placement

            // Cancel any half-typed chord on the way past, whatever the key turns
            // out to be.
            let takeBannerDown = if armed then [ Effect.Prompt false ] else []

            // Placement is checked first: its prefix is held down, so the keystroke
            // is unambiguous the moment the modifiers match.
            if held = placement.Prefix.Modifiers then
                let resolve effect = swallow false (takeBannerDown @ [ effect ])

                match placement.Zones |> List.tryFind (fun zone -> zone.Key = vk) with
                | Some zone -> resolve (Effect.SnapTo zone)
                | None when placement.NextMonitorVk = Some vk -> resolve Effect.NextMonitor
                | None when placement.CycleDisplayVk = Some vk -> resolve Effect.CycleDisplay
                | None when placement.ApplyLayoutVk = Some vk -> resolve Effect.ApplyLayout

                // An unmapped key still ends a chord that is open: the banner comes
                // down and the keystroke stops here rather than reaching the window
                // underneath.
                | None when armed -> swallow false takeBannerDown

                | None -> passOn

            elif armed then
                // The banner is up. Every path from here takes it down.
                match launcher.Apps |> List.tryFind (fun (chordKey, _) -> chordKey = vk) with
                | Some(_, target) -> swallow false [ Effect.Prompt false; Effect.Toggle target ]

                // Nothing is bound to this key, so it belongs to the chord rather
                // than to the application: Escape, the prefix again and every other
                // key alike dismiss the banner and stop here. Passing the key on
                // instead let a mistyped chord fire whatever that key means in the
                // window underneath.
                | None -> swallow false [ Effect.Prompt false ]

            elif held = launcher.Prefix.Modifiers
                 && launcher.Prefix.Key = Some vk
                 && not (inPassThroughApp launcher.PassThroughIn) then
                swallow true [ Effect.Prompt true ]

            else
                passOn

// WHEN THE PREFIX DOES NOTHING
//
// Three ways Ctrl+K can go missing, and each leaves a different line in the log.
// They exist to tell this program's faults from those of a key remapper (PowerToys
// Keyboard Manager turning Caps Lock into Ctrl, say), which works by swallowing the
// real key and injecting the one it stands for.
//
// * The hook ran late. Its thread was busy, Windows stopped waiting, and the key
//   went on without it - this program's fault.
// * The prefix key came with extra modifiers held. If only Ctrl was down, something
//   has left a modifier stuck.
// * The prefix key came before the remapper's Ctrl did, so it arrived as a plain
//   letter. Someone in the chain of hooks was slow enough for the order to flip.

/// How late the hook may run before it is worth a line. Windows' patience
/// (LowLevelHooksTimeout) is a few hundred milliseconds.
let private lateMs = 150u

/// What the diagnostics remember from one keystroke to the next. Times are
/// milliseconds since boot, as Windows stamps keystrokes.
type internal Watch =
    { /// No more than one "ran late" line per stall: every key typed during one
      /// is late.
      LastLateNote : uint32
      /// When the prefix key last went past as a plain letter, for comparing with
      /// the next injected Ctrl.
      PlainPrefixAt : uint32 option }

let internal unwatched = { LastLateNote = 0u; PlainPrefixAt = None }

let private isCtrl vk =
    match enum<Keys> vk with
    | Keys.ControlKey
    | Keys.LControlKey
    | Keys.RControlKey -> true
    | _ -> false

/// What a key going down says about the three failures, as lines for the log. As
/// with `step`, nothing here touches Windows: `pressedAt` is the keystroke's own
/// time, `now` the hook's, and the held modifiers are only asked for when they
/// matter.
let internal watch
    (settings: Settings option)
    (watching: Watch)
    vk
    (pressedAt: uint32)
    (now: uint32)
    injected
    swallowed
    (held: unit -> Set<Modifier>)
    =
    let late = now - pressedAt

    let watching, lateNotes =
        if not injected && late > lateMs && now - watching.LastLateNote > 5000u then
            { watching with LastLateNote = now },
            [ $"Ran {late} ms after a key was pressed. Keys pressed meanwhile may have gone on without this program seeing them." ]
        else
            watching, []

    let plainPrefixAt, prefixNotes =
        match settings with
        | Some _ when injected && isCtrl vk ->
            match watching.PlainPrefixAt with
            | Some at when now - at < 100u ->
                None,
                [ "The prefix key arrived before an injected Ctrl, so it went through as a plain letter. A key remapper's Ctrl was overtaken." ]
            | _ -> None, []

        | Some settings when not injected && not (isModifier vk) ->
            if settings.Launcher.Prefix.Key = Some vk && not swallowed then
                let held = held ()
                let wanted = settings.Launcher.Prefix.Modifiers

                if held.IsEmpty then
                    Some now, []
                elif Set.isSubset wanted held && held <> wanted then
                    let names = held |> Seq.map string |> String.concat "+"
                    None,
                    [ $"The prefix key arrived with {names} held, so the chord did not open. If only the prefix's own modifiers were down, one is stuck." ]
                else
                    None, []
            else
                None, []

        | _ -> watching.PlainPrefixAt, []

    { watching with PlainPrefixAt = plainPrefixAt }, lateNotes @ prefixNotes

let mutable private watching = unwatched

// REMAPPING A KEY
//
// A low-level hook cannot change a keystroke, only stop it. So a remap stops the
// key pressed and injects the one it stands for, with SendInput - which is all
// PowerToys Keyboard Manager does, and where it goes wrong. An injection joins the
// back of the input queue. If the hook was slow to see Caps Lock, the D typed a
// moment later is already in that queue, ahead of the Ctrl standing in for Caps
// Lock, and the application gets a plain D followed by a Ctrl with nothing to
// modify.
//
// Doing the remap here lets the hook notice. It sees every key in the order it
// was typed, and it knows which of its injections have not come back through it
// yet. A key that arrives while one is still out overtook it, so the hook stops
// that key too and injects it again, behind. Order is kept and the D arrives as
// Ctrl+D.
//
// Its own injections are marked (dwExtraInfo) so they can be told from anyone
// else's. Each is the stand-in for something actually typed, so past this point
// it counts as typed: the chord sees a remapped or re-sent key exactly as it
// would a real one.

/// What this program writes in dwExtraInfo on everything it injects. Any value
/// will do that nothing else is likely to choose; this is "MASH" in ASCII.
let private marker = 0x4D415348n

/// What the remap remembers from one keystroke to the next.
type internal Remapping =
    { /// Each remapped key held down, and the key sent for it. Its release sends
      /// the release of that key, whatever the configuration says by then, so a
      /// reload can never leave a Ctrl held down.
      Held : Map<int, int>
      /// Injections not yet seen coming back through the hook.
      Outstanding : int
      /// When the most recent of them went out, by the time of the key that sent it.
      SentAt : uint32 }

let internal unremapped = { Held = Map.empty; Outstanding = 0; SentAt = 0u }

/// An injection that has not come back in this long never will: something
/// further along the chain of hooks swallowed it. Waiting any longer would only
/// hold up every key typed after it.
let private lostMs = 1000u

/// What the hook injects in place of the key it was handed.
[<RequireQualifiedAccess>]
type internal Send =
    /// The key a remapped one stands for.
    | Key of vk: int * up: bool
    /// The key itself, again, to queue behind what was sent before it.
    | Again

/// A key going down or up, before the chord looks at it. Some means the key stops
/// here and that is what goes in its place; None means it carries on as it is.
let internal remap (table: Map<int, int>) (remapping: Remapping) vk up injected ours (time: uint32) =
    let send held what =
        { Held = held; Outstanding = remapping.Outstanding + 1; SentAt = time }, Some what

    if ours then
        { remapping with Outstanding = max 0 (remapping.Outstanding - 1) }, None

    // Someone else's injection - including another remapper's. Turning those round
    // too would only fight it.
    elif injected then
        remapping, None

    else
        match remapping.Held.TryFind vk, table.TryFind vk with
        | Some sent, _ when up -> send (remapping.Held.Remove vk) (Send.Key(sent, true))

        // Auto-repeat, which Ctrl does too.
        | Some sent, _ -> send remapping.Held (Send.Key(sent, false))

        | None, Some target when not up -> send (remapping.Held.Add(vk, target)) (Send.Key(target, false))

        // Everything else, including the release of a key pressed before the remap
        // was there to take it. It goes on as it is unless it would overtake.
        | _ ->
            if remapping.Outstanding = 0 then
                remapping, None
            elif time - remapping.SentAt > lostMs then
                { remapping with Outstanding = 0 }, None
            else
                send remapping.Held Send.Again

let mutable private remapping = unremapped

/// The remap profile that fits, chosen the way a layout is: first match wins.
let internal chooseRemap (profiles: RemapProfile list) (holding: Conditions -> bool) =
    profiles |> List.tryFind (fun profile -> holding profile.When)

/// Which remap to put in force, and a line for the log if that is a change from
/// the profile named `before`. Only a change is worth a line: the hardware
/// broadcasts that lead here fire for every USB device plugged in.
let internal rechoose (profiles: RemapProfile list) (holding: Conditions -> bool) (before: string option) =
    let chosen = chooseRemap profiles holding
    let keys = chosen |> Option.map (fun profile -> profile.Keys) |> Option.defaultValue Map.empty
    let name = chosen |> Option.map (fun profile -> profile.Name)

    let note =
        match chosen with
        | _ when name = before -> None
        | Some profile when profile.Keys.IsEmpty -> Some $"'{profile.Name}' is in force, so no keys are remapped."
        | Some profile -> Some $"'{profile.Name}' is in force, remapping {profile.Keys.Count} keys."
        | None when List.isEmpty profiles -> None
        | None -> Some "No remap profile fits what is plugged in, so no keys are remapped."

    keys, name, note

/// The keys remapped right now. The hook only reads this; `rekey` decides it.
let mutable private keysInForce : Map<int, int> = Map.empty
let mutable private profileInForce : string option = None

/// Puts in force the remap profile that fits what is plugged in. Choosing can walk
/// the whole device tree, which has no place inside the hook - so this runs on the
/// thread pool when the configuration is read and when the hardware changes, and
/// the hook picks up the result on its next key. A reference assignment is atomic,
/// so the worst a keystroke sees is the remap from a moment ago, and a key held
/// across the change is still released as the key it was sent as.
let rekey () =
    try
        let profiles =
            match Config.current with
            | Some settings -> settings.Remap
            | None -> []

        // Without profiles there is nothing to ask, and no reason to look at the
        // hardware to find that out.
        let holding = if List.isEmpty profiles then (fun _ -> false) else Layout.conditionsNow ()
        let keys, name, note = rechoose profiles holding profileInForce

        keysInForce <- keys
        profileInForce <- name
        note |> Option.iter (Log.note "remap")
    with error ->
        Log.write "remap" error

/// One keystroke, marked as this program's. The scan code goes in as well as the
/// virtual key, because some applications - terminals among them - read that
/// instead.
let private keystroke vk (scan: uint32) extended up =
    let mutable key = KEYBDINPUT()
    key.wVk <- uint16 vk
    key.wScan <- uint16 (scan &&& 0xFFu)
    key.dwFlags <- (if extended then KEYEVENTF_EXTENDEDKEY else 0u) ||| (if up then KEYEVENTF_KEYUP else 0u)
    key.dwExtraInfo <- marker

    let mutable input = INPUT()
    input.``type`` <- INPUT_KEYBOARD
    input.ki <- key
    input

let private inject (input: INPUT) =
    if SendInput(1u, [| input |], Marshal.SizeOf<INPUT>()) = 0u then
        let code = Marshal.GetLastWin32Error()
        Task.Run(fun () -> Log.note "keyboard hook" $"SendInput refused a keystroke (error {code}).") |> ignore

/// A key standing in for another. Its scan code comes from the keyboard layout,
/// with the E0 byte that marks right Ctrl from left in the high half.
let private sendKey vk up =
    let scan = MapVirtualKey(uint32 vk, MAPVK_VK_TO_VSC_EX)
    let extended = (scan >>> 8) = 0xE0u || (scan >>> 8) = 0xE1u
    inject (keystroke vk scan extended up)

/// Lets go of every remapped key still held, so that leaving does not leave a Ctrl
/// down behind it.
let private releaseHeld () =
    for KeyValue(_, sent) in remapping.Held do
        sendKey sent true

    remapping <- unremapped

/// Runs on the hook's own thread. Keep it cheap: Windows silently drops a
/// low-level hook that takes too long to return.
let private onKey (handlers: Handlers) nCode (wParam: nativeint) (lParam: nativeint) =
    // The return value is the whole protocol: pass the event down the chain of
    // hooks (other programs may have installed their own) or return non-zero to
    // consume it. wParam carries which message it is, lParam points at the
    // KBDLLHOOKSTRUCT describing the key.
    let swallow = 1n
    let passOn () = CallNextHookEx(hook, nCode, wParam, lParam)

    let perform effect =
        match effect with
        | Effect.Prompt visible -> handlers.Prompt visible
        | Effect.Toggle target -> handlers.Toggle target
        | Effect.SnapTo zone -> handlers.SnapTo zone
        | Effect.NextMonitor -> handlers.NextMonitor()
        | Effect.CycleDisplay -> handlers.CycleDisplay()
        | Effect.ApplyLayout -> handlers.ApplyLayout()

    // Nothing may escape this function. Windows calls it across a native boundary,
    // where an exception does not reach the message loop's handler - it terminates
    // the process, and from the outside that looks like the daemon vanishing.
    try
        let message = int wParam
        let isDown = message = WM_KEYDOWN || message = WM_SYSKEYDOWN
        let isUp = message = WM_KEYUP || message = WM_SYSKEYUP

        if nCode <> HC_ACTION || not (isDown || isUp) then
            passOn ()
        else
            // lParam is a pointer into unmanaged memory. Marshal.PtrToStructure
            // copies those bytes into a managed struct, laid out per the
            // StructLayout attribute declared on it.
            let key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam)
            let vk = int key.vkCode
            let injected = (key.flags &&& LLKHF_INJECTED) <> 0u
            let ours = injected && key.dwExtraInfo = marker

            let remapped, sending = remap keysInForce remapping vk isUp injected ours key.time
            remapping <- remapped

            // Past the remap, this program's own injections count as typed.
            let injected = injected && not ours

            match sending with
            | Some(Send.Key(target, up)) ->
                sendKey target up
                swallow

            | Some Send.Again ->
                inject (keystroke vk key.scanCode ((key.flags &&& LLKHF_EXTENDED) <> 0u) isUp)
                swallow

            | None ->
                if isUp then
                    swallowedHeld <- released swallowedHeld vk
                    passOn ()

                // The press already did what it meant; its repeats stop here with it.
                elif not injected && isRepeat swallowedHeld vk key.time then
                    swallowedHeld <- Some(vk, key.time)
                    swallow

                else
                    let outcome = step Config.current armed vk injected heldModifiers isPassThroughApp
                    armed <- outcome.Armed

                    swallowedHeld <- pressed swallowedHeld vk key.time injected outcome.Swallow

                    let now = uint32 Environment.TickCount
                    let watched, notes = watch Config.current watching vk key.time now injected outcome.Swallow heldModifiers
                    watching <- watched

                    // File I/O has no place inside the hook, so the writing happens elsewhere.
                    for note in notes do
                        Task.Run(fun () -> Log.note "keyboard hook" note) |> ignore

                    outcome.Effects |> List.iter perform
                    if outcome.Swallow then swallow else passOn ()

    with error ->
        // Leave the latch clear so a failure cannot stick the chord armed forever.
        Log.write "keyboard hook" error
        armed <- false
        passOn ()

/// The thread the hook is delivered to, once it is running.
let mutable private hookThread : Thread = null
let mutable private hookThreadId = 0u

/// Installs the keyboard hook, on a thread of its own.
///
/// Its own, rather than the UI thread, because the UI thread is where windows get
/// focused and moved, and either can block on an application that is slow to
/// answer. Every keystroke typed meanwhile would wait with it, and a remapped
/// Caps Lock that waits is a Ctrl that arrives after the key it was meant to
/// modify. This thread does nothing but run the hook, and asks for the CPU first.
let install (handlers: Handlers) =
    callback <- LowLevelKeyboardProc(fun nCode wParam lParam -> onKey handlers nCode wParam lParam)

    let installed = TaskCompletionSource<Result<unit, int>>()

    let run () =
        hookThreadId <- GetCurrentThreadId()

        // The final 0u is the thread id to hook: zero means "all threads", i.e. the
        // whole desktop. Windows signals failure by returning a null handle, with
        // the reason in the thread's last-error code - the .NET way to read that is
        // Marshal.GetLastWin32Error, which works because the declaration above sets
        // SetLastError = true.
        hook <- SetWindowsHookEx(WH_KEYBOARD_LL, callback, GetModuleHandle(null), 0u)

        if hook = 0n then
            installed.SetResult(Error(Marshal.GetLastWin32Error()))
        else
            installed.SetResult(Ok())

            // Keystrokes arrive as messages, so this thread needs a loop to receive
            // them, the same as the UI thread does. It runs until uninstall posts
            // WM_QUIT, and the cleanup is done here because a hook belongs to the
            // thread that installed it.
            Application.Run()
            releaseHeld ()
            UnhookWindowsHookEx(hook) |> ignore
            hook <- 0n

    // A background thread does not keep the process alive on its own, so an exit
    // that skips uninstall is not held up by it.
    hookThread <- Thread(ThreadStart run, IsBackground = true, Priority = ThreadPriority.Highest, Name = "keyboard hook")
    hookThread.SetApartmentState(ApartmentState.STA)
    hookThread.Start()
    installed.Task.Result

let uninstall () =
    if hook <> 0n then
        PostThreadMessage(hookThreadId, WM_QUIT, 0n, 0n) |> ignore
        hookThread.Join(TimeSpan.FromSeconds 1.0) |> ignore
