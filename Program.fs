// 10 of 10 - the entry point
//
// Mashed Potato - a tiny AutoHotkey-style chord hotkey daemon for Windows.
//
// Two kinds of binding, both defined in mashedpotato.json rather than here:
//
//   launcher    a chord - a prefix such as Ctrl+K, released, then a key that brings
//               an application up, focuses it, or minimizes it if it is already in
//               front. While the prefix is armed an "App Switcher" banner sits
//               across the bottom of the screen until the chord resolves; Escape or
//               the prefix again dismisses it, any other key cancels and goes on to
//               the application.
//
//   placement   a held combination such as Ctrl+Shift+Alt, plus a key that moves the
//               current window into a region of its monitor, or to the next monitor.
//
// There are no bindings in the code at all - see Config.fs. Runs in the tray with
// no console window. Build with ./build.sh

module MashedPotato.Program

open System
open System.Threading
open System.Windows.Forms

[<EntryPoint; STAThread>]
let main _ =
    // Two last-resort nets. AppDomain.UnhandledException fires for an exception that
    // escapes any thread (it cannot stop the process, only observe it on the way out);
    // Application.ThreadException fires for one that escapes a WinForms message-loop
    // callback, which WinForms catches rather than letting it kill the app. Between
    // them they make failures visible in the log instead of silent.
    AppDomain.CurrentDomain.UnhandledException.Add(fun args ->
        match args.ExceptionObject with
        | :? exn as error -> Log.write "unhandled" error
        | other -> Log.write "unhandled" (Exception(string other)))

    Application.ThreadException.Add(fun args -> Log.write "ui thread" args.Exception)

    // A named Mutex is a kernel object visible to every process on the machine, so
    // asking for one by name is the standard single-instance check: whoever creates it
    // first is told so, and everyone else knows a copy is already running. `use` keeps
    // it alive for the process and releases it on exit.
    let mutable isFirstInstance = false
    use _mutex = new Mutex(true, "MashedPotato.SingleInstance.v1", &isFirstInstance)
    if isFirstInstance then Daemon.run () else 0
