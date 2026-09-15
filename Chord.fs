// 9 of 12 - the keyboard hook
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
//   thread's message queue. So the callback runs on our UI thread, and the app
//   needs a running message loop for keystrokes to arrive at all.
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

let private isModifier vk =
    match enum<Keys> vk with
    | Keys.ShiftKey
    | Keys.ControlKey
    | Keys.Menu
    | Keys.LWin
    | Keys.RWin -> true
    | key -> key >= Keys.LShiftKey && key <= Keys.RMenu

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

/// Runs on whatever thread the OS delivers keystrokes to. Keep it cheap: Windows
/// silently drops a low-level hook that takes too long to return.
let private onKey (handlers: Handlers) nCode (wParam: nativeint) (lParam: nativeint) =
    // The return value is the whole protocol: pass the event down the chain of
    // hooks (other programs may have installed their own) or return non-zero to
    // consume it. wParam carries which message it is, lParam points at the
    // KBDLLHOOKSTRUCT describing the key.
    let swallow = 1n
    let passOn () = CallNextHookEx(hook, nCode, wParam, lParam)

    // Nothing may escape this function. Windows calls it across a native boundary,
    // where an exception does not reach the message loop's handler - it terminates
    // the process, and from the outside that looks like the daemon vanishing.
    try
        let message = int wParam

        if nCode <> HC_ACTION || (message <> WM_KEYDOWN && message <> WM_SYSKEYDOWN) then
            passOn ()
        else
            // lParam is a pointer into unmanaged memory. Marshal.PtrToStructure
            // copies those bytes into a managed struct, laid out per the
            // StructLayout attribute declared on it.
            let key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam)
            let vk = int key.vkCode

            // Ignore synthetic input, and let modifiers alone keep the chord open so
            // that "Ctrl+K, Ctrl+C" behaves the same as "Ctrl+K, C".
            if (key.flags &&& LLKHF_INJECTED) <> 0u || isModifier vk then
                passOn ()
            else
                match Config.current with
                // No configuration read, so nothing here is ours to take.
                | None -> passOn ()

                | Some settings ->
                    let held = heldModifiers ()
                    let launcher = settings.Launcher
                    let placement = settings.Placement

                    // Cancel any half-typed chord on the way past, whatever the key
                    // turns out to be.
                    let takeBannerDown () =
                        if armed then
                            armed <- false
                            handlers.Prompt false

                    // Placement is checked first: its prefix is held down, so the
                    // keystroke is unambiguous the moment the modifiers match.
                    if held = placement.Prefix.Modifiers then
                        match placement.Zones |> List.tryFind (fun zone -> zone.Key = vk) with
                        | Some zone ->
                            takeBannerDown ()
                            handlers.SnapTo zone
                            swallow

                        | None when placement.NextMonitorVk = Some vk ->
                            takeBannerDown ()
                            handlers.NextMonitor()
                            swallow

                        | None when placement.CycleDisplayVk = Some vk ->
                            takeBannerDown ()
                            handlers.CycleDisplay()
                            swallow

                        | None when placement.ApplyLayoutVk = Some vk ->
                            takeBannerDown ()
                            handlers.ApplyLayout()
                            swallow

                        // An unmapped key still ends a chord that is open: the
                        // banner comes down and the keystroke stops here rather than
                        // reaching the window underneath.
                        | None when armed ->
                            takeBannerDown ()
                            swallow

                        | None -> passOn ()

                    elif armed then
                        // The banner is up. Every path from here takes it down.
                        let disarm () =
                            armed <- false
                            handlers.Prompt false

                        match launcher.Apps |> List.tryFind (fun (chordKey, _) -> chordKey = vk) with
                        | Some(_, target) ->
                            disarm ()
                            handlers.Toggle target
                            swallow

                        // Nothing is bound to this key, so it belongs to the chord
                        // rather than to the application: Escape, the prefix again
                        // and every other key alike dismiss the banner and stop
                        // here. Passing the key on instead let a mistyped chord fire
                        // whatever that key means in the window underneath.
                        | None ->
                            disarm ()
                            swallow

                    elif held = launcher.Prefix.Modifiers
                         && launcher.Prefix.Key = Some vk
                         && not (isPassThroughApp launcher.PassThroughIn) then
                        armed <- true
                        handlers.Prompt true
                        swallow

                    else
                        passOn ()

    with error ->
        // Leave the latch clear so a failure cannot stick the chord armed forever.
        Log.write "keyboard hook" error
        armed <- false
        passOn ()

/// Installs the keyboard hook.
let install (handlers: Handlers) =
    callback <- LowLevelKeyboardProc(fun nCode wParam lParam -> onKey handlers nCode wParam lParam)

    // The final 0u is the thread id to hook: zero means "all threads", i.e. the
    // whole desktop. Windows signals failure by returning a null handle, with the
    // reason in the thread's last-error code - the .NET way to read that is
    // Marshal.GetLastWin32Error, which works because the declaration above sets
    // SetLastError = true.
    hook <- SetWindowsHookEx(WH_KEYBOARD_LL, callback, GetModuleHandle(null), 0u)
    if hook = 0n then Error(Marshal.GetLastWin32Error()) else Ok()

let uninstall () =
    if hook <> 0n then
        UnhookWindowsHookEx(hook) |> ignore
        hook <- 0n
