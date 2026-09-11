// 5 of 10 - launching, focusing and minimizing an application
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

open Interop

// System.Diagnostics.Process is .NET's view of a running program. It is a
// disposable wrapper around an OS handle, so every Process object obtained here
// gets disposed - see runningPids.

/// Set by the host so failures can surface as a tray balloon.
let mutable onError : string -> unit = ignore

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

/// Launch, focus or minimize - whichever the app's current state calls for.
let activate (target: Target) =
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
