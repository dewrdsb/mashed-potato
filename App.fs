// 6 of 13 - launching, focusing and minimizing an application
//
// Finding the window that belongs to an app, which is harder than it sounds, and
// deciding what to do with it.
//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

module MashedPotato.App

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

open Interop

// System.Diagnostics.Process is .NET's view of a running program. It is a
// disposable wrapper around an OS handle, so every Process object obtained here
// gets disposed - see runningPids.

/// Set by the host so failures can surface as a tray balloon.
let mutable onError : string -> unit = ignore

/// Set by the host: runs work on the message loop. Waiting for a launched window
/// happens on the thread pool - it takes seconds - but the focusing itself belongs
/// on the UI thread, with everything else here that touches a window.
let mutable onLoop : (unit -> unit) -> unit = fun work -> work ()

/// GetProcessesByName matches on the image name without ".exe", and returns every
/// match - a modern browser is dozens of processes, all with the same name. Each
/// returned Process holds a handle, hence the `finally` that disposes them all.
let private runningPids (target: Target) =
    let processes = Process.GetProcessesByName(target.ProcessName)

    try
        processes |> Array.map (fun p -> p.Id) |> Set.ofArray
    finally
        processes |> Array.iter (fun p -> p.Dispose())

/// An app's real windows, topmost first. Chrome and Spotify both keep a pile of
/// invisible, untitled and owned helper windows around - Spotify even has one
/// titled "GDI+ Window (Spotify.exe)" - so Process.MainWindowHandle is not
/// trustworthy. Keep only windows a person could actually be looking at.
let private windowsOf (pids: Set<int>) =
    // The desktop belongs to explorer.exe and looks exactly like a real window to
    // every test below, so binding Explorer would otherwise "switch to" the
    // desktop - and never launch, because a window was found.
    let desktop = GetShellWindow()

    topLevelWindows ()
    |> List.filter (fun hwnd ->
        hwnd <> desktop
        && IsWindowVisible(hwnd)
        && GetWindow(hwnd, GW_OWNER) = 0n
        && GetWindowTextLength(hwnd) > 0
        && pids.Contains(processIdOf hwnd))

/// The window to act on: the topmost one that is not minimized, or - when every
/// window is minimized - the topmost of those. Chrome routinely has several real
/// windows, and Z-order is what makes "the one you were last using" win.
let private pick windows =
    match windows |> List.tryFind (fun hwnd -> not (IsIconic(hwnd))) with
    | Some hwnd -> Some hwnd
    | None -> List.tryHead windows

let private focus (hwnd: nativeint) =
    ShowWindow(hwnd, (if IsIconic(hwnd) then SW_RESTORE else SW_SHOW)) |> ignore

    // Windows refuses foreground changes from a process that has not seen recent
    // input, so borrow the current foreground thread's input queue first. The
    // restriction is enforced per input queue: attaching ours to theirs makes Windows
    // treat us as part of the same input context, and the call is allowed. Attaching
    // is symmetric and must be undone immediately - leaving two threads attached makes
    // each wait on the other's input.
    //
    // Doing this *before* the first attempt rather than after a failure is what stops
    // the taskbar flickering. A refused SetForegroundWindow does not fail quietly:
    // Windows flashes the target's taskbar button instead, and an auto-hide taskbar
    // slides out to show the flash before hiding again. The window still ends up
    // focused either way, so the old order looked correct and merely flickered.
    let mutable unused = 0u
    let foreignThread = GetWindowThreadProcessId(GetForegroundWindow(), &unused)
    let ownThread = GetCurrentThreadId()

    let borrowed =
        foreignThread <> 0u
        && foreignThread <> ownThread
        && AttachThreadInput(ownThread, foreignThread, true)

    BringWindowToTop(hwnd) |> ignore
    SetForegroundWindow(hwnd) |> ignore

    if borrowed then
        AttachThreadInput(ownThread, foreignThread, false) |> ignore

let private launch (target: Target) =
    let command =
        target.ExePaths
        |> List.tryFind File.Exists
        |> Option.defaultValue target.ShellFallback

    try
        // UseShellExecute = true routes through ShellExecute, the same machinery
        // the Start menu uses, rather than creating the process directly. That is
        // what makes non-path targets work: a URI scheme like "spotify:", or a
        // bare "chrome.exe" resolved through the App Paths registry key. With it
        // false, .NET would demand a real executable path.
        Process.Start(ProcessStartInfo(command, UseShellExecute = true)) |> ignore
    with ex ->
        onError $"Could not start {target.Name}: {ex.Message}"

/// Every window of an app a person could be looking at, topmost first. A layout
/// asking for all of them takes this; there is no way to name them individually in
/// a configuration file, because they are documents and they come and go.
let windowsFor (target: Target) =
    let pids = runningPids target
    if pids.IsEmpty then [] else windowsOf pids

/// The window a layout should move: the same one `activate` would act on, found
/// without touching it. None when nothing of that name is running, or when it is
/// running with no window a person could be looking at - closed to the tray.
let windowOf (target: Target) = pick (windowsFor target)

/// How long to keep looking for the window of an app that has just been launched.
/// Long enough for a cold start that has to bring up WSL and a language server;
/// short enough that a window appearing after it is the user's own doing and should
/// not be snatched in front of whatever they moved on to.
let private patience = TimeSpan.FromSeconds 10.0
let private lookAgainMs = 20

/// Where the wanted tab is, across every window the application has open. A browser
/// with three windows can have the tab in any of them, and the answer has to be the
/// window as well as the tab.
let private tabIn (target: Target) wanted =
    windowsFor target
    |> List.tryPick (fun hwnd -> Tabs.find wanted hwnd |> Option.map (fun tab -> hwnd, tab))

/// Brings a browser window to the front and only then switches its tab. The order
/// is not cosmetic. Selecting a tab in a window that is in the background, then
/// focusing it, leaves Caps Lock switched on for anyone whose Caps Lock is remapped
/// to Ctrl by PowerToys Keyboard Manager and still held from the chord - every
/// time, measured. Focusing first and selecting second never did. Waiting for the
/// focus to finish matters too, since onLoop only queues it; the wait is bounded so
/// a hung message loop cannot keep the tab from switching.
let private focusThenSelect hwnd (tab: Tabs.Tab) =
    // Not `use`: after a timeout the queued focus still calls Set, and must not
    // find it disposed. It holds no OS handle unless one is asked for.
    let focused = new ManualResetEventSlim(false)

    onLoop (fun () ->
        try
            focus hwnd
        finally
            focused.Set())

    focused.Wait(TimeSpan.FromSeconds 1.0) |> ignore
    tab.Select()

/// Polls until `look` finds something or the deadline passes.
let private waitFor (deadline: DateTime) look =
    let mutable found = None

    while found.IsNone && DateTime.UtcNow < deadline do
        found <- look ()
        if found.IsNone then Thread.Sleep lookAgainMs

    found

/// Launching an application does not give it the right to come to the front.
///
/// Windows grants the foreground to a process that already has it, or to one the
/// foreground process started. Mashed is neither: the chord fires while the user is
/// in some other window, so a program started here is refused the foreground and
/// gets a flashing taskbar button instead. Most of the applications bound in the
/// shipped configuration paper over this themselves - Chromium and Electron do the
/// same AttachThreadInput dance `focus` does - and the ones that do not, like
/// Neovide, simply ask once, are told no, and open behind whatever was already
/// there.
///
/// So the window is waited for and focused deliberately, through the same path a
/// second press of the chord would take. Nothing happens if the application managed
/// it on its own, which is why this changed nothing for the apps that already worked.
let private settleAfterLaunch (target: Target) =
    Task.Run(fun () ->
        try
            let deadline = DateTime.UtcNow + patience

            match waitFor deadline (fun () -> windowOf target) with
            | None ->
                Log.note
                    "activate"
                    $"{target.Name}: launched, but no window of a process named '{target.ProcessName}' appeared within {patience.TotalSeconds} seconds, so there was nothing to focus."

            | Some hwnd ->
                match target.Tab with
                | None ->
                    // It got there by itself: leave it be.
                    if GetForegroundWindow() <> hwnd then onLoop (fun () -> focus hwnd)

                | Some wanted ->
                    // A browser has a window well before it has the tabs its last
                    // session ended with, so the tab is waited for separately - and
                    // against the same deadline, which is what stops a browser that
                    // never restores the tab from being waited on twice over.
                    match waitFor deadline (fun () -> tabIn target wanted) with
                    | Some(holder, tab) -> focusThenSelect holder tab

                    | None ->
                        Log.note "activate" $"{target.Name}: opened, but no tab matching '{wanted}' appeared, so the window was focused as it was."
                        onLoop (fun () -> focus hwnd)
        with error ->
            Log.write "activate" error)
    |> ignore

/// Launch, focus or minimize - whichever the app's current state calls for, and for
/// a binding that names a tab, which tab as well.
let rec activate (target: Target) =
    match target.Tab with
    | Some wanted -> toTab target wanted
    | None -> toApp target

/// Reading a tab strip is cross-process COM and costs a couple of hundred
/// milliseconds, so none of it runs on the message loop - that being the thread the
/// keyboard hook is delivered to. Only the focusing goes back there.
and private toTab (target: Target) wanted =
    Task.Run(fun () ->
        try
            match tabIn target wanted with
            // Already looking at it, so the second press means the same thing a
            // second press always means here: get out of the way.
            | Some(hwnd, tab) when tab.IsSelected && GetForegroundWindow() = hwnd ->
                onLoop (fun () -> ShowWindow(hwnd, SW_MINIMIZE) |> ignore)

            | Some(hwnd, tab) -> focusThenSelect hwnd tab

            | None ->
                if not (List.isEmpty (windowsFor target)) then
                    Log.note "activate" $"{target.Name}: nothing open has a tab matching '{wanted}', so this was a switch to the application."

                onLoop (fun () -> toApp target)
        with error ->
            Log.write "activate" error
            onLoop (fun () -> toApp target))
    |> ignore

and private toApp (target: Target) =
    try
        let pids = runningPids target
        let windows = if pids.IsEmpty then [] else windowsOf pids

        match pick windows with
        // No window at all. Two quite different reasons land here, and telling them
        // apart in the log is the difference between "it genuinely was not running"
        // and "processName does not match anything, so this will never switch".
        | None ->
            if pids.IsEmpty then
                Log.note
                    "activate"
                    $"{target.Name}: launching - no running process is named '{target.ProcessName}'. If it IS running, check processName: it must be the image name from Task Manager's Details tab, without .exe (VS Code is 'Code', not 'Visual Studio Code')."
            else
                Log.note
                    "activate"
                    $"{target.Name}: launching - {pids.Count} process(es) named '{target.ProcessName}' are running but none has a visible window, so it is probably closed to the tray."

            launch target
            settleAfterLaunch target

        // Every window minimized. This has to be matched before the foreground
        // test, because SW_MINIMIZE does not hand the foreground to anyone else -
        // GetForegroundWindow() goes on naming the window that was just minimized,
        // so testing "is it in front?" first would re-minimize it and do nothing.
        | Some hwnd when IsIconic(hwnd) -> focus hwnd

        | Some hwnd when pids.Contains(processIdOf (GetForegroundWindow())) ->
            ShowWindow(hwnd, SW_MINIMIZE) |> ignore

        | Some hwnd -> focus hwnd
    with ex ->
        onError $"{target.Name}: {ex.Message}"
