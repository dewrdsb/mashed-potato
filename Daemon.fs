// 10 of 11 - assembling the program
//
// Tray icon, hidden message-pump window, hook installation and the message loop.
// The first file that knows about all the others.
//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

module internal MashedPotato.Daemon

open System
open System.Diagnostics
open System.Drawing
open System.Windows.Forms

// This module assembles the app: a tray icon, a hidden window used as a way to get
// work onto the UI thread, the keyboard hook, and the message loop that drives all
// of it. There is no visible main window - the banner is shown and hidden, and
// nothing else ever appears.

/// The .ico is embedded rather than read from beside the executable: a tray icon
/// that depends on a loose file is one missing file away from a blank square.
/// Icon(stream, size) picks the frame nearest the size asked for, and the shell
/// wants SmallIconSize - 16px at 100% scaling, 20 at 125%, 24 at 150%, which is
/// why the file carries a frame at each.
let private trayIcon () =
    let assembly = Reflection.Assembly.GetExecutingAssembly()
    use stream = assembly.GetManifestResourceStream("mashed.ico")
    new Icon(stream, SystemInformation.SmallIconSize)

/// Prefer the character a key types, so the menu reads "Ctrl+Shift+Alt+[" rather
/// than "Ctrl+Shift+Alt+OemOpenBrackets". Keys with no character - F1, Escape -
/// map to 0 and fall back to the enum name.
let private keyName vk =
    let mapped = char (Interop.MapVirtualKey(uint32 vk, Interop.MAPVK_VK_TO_CHAR) &&& 0xFFFFu)

    if Char.IsLetterOrDigit mapped || Char.IsPunctuation mapped || Char.IsSymbol mapped then
        string mapped
    else
        (enum<Keys> vk).ToString()

let private modifierName modifier =
    match modifier with
    | Modifier.Ctrl -> "Ctrl"
    | Modifier.Shift -> "Shift"
    | Modifier.Alt -> "Alt"
    | Modifier.Win -> "Win"

/// Renders a prefix the way the config spells it: "Ctrl+K", "Ctrl+Shift+Alt".
/// Modifiers come out in a fixed order so the label does not depend on how the set
/// happens to enumerate.
let private prefixText (prefix: Prefix) =
    let modifiers =
        [ Modifier.Ctrl; Modifier.Shift; Modifier.Alt; Modifier.Win ]
        |> List.filter prefix.Modifiers.Contains
        |> List.map modifierName

    let parts =
        match prefix.Key with
        | Some vk -> modifiers @ [ keyName vk ]
        | None -> modifiers

    String.Join("+", parts)

/// Rebuilt on every reload, so it always shows what the file currently says.
///
/// NotifyIcon is WinForms' wrapper over the notification area ("system tray"). It
/// is not a window - it is an icon registered with the shell, which raises .NET
/// events for clicks and can show balloon notifications. ContextMenuStrip is the
/// menu it pops up on right-click; items with Enabled = false are the standard way
/// to put non-clickable captions in one.
let private buildTrayMenu (onReload: unit -> unit) =
    let menu = new ContextMenuStrip()

    let caption (text: string) =
        menu.Items.Add(new ToolStripMenuItem(text, Enabled = false)) |> ignore

    match Config.current with
    | None -> caption "no configuration loaded"
    | Some settings ->
        // A chord prefix is shown with a comma - press, release, then the key. A
        // held prefix is shown with a plus, because it stays down.
        for (vk, target) in settings.Launcher.Apps do
            caption $"{prefixText settings.Launcher.Prefix}, {keyName vk}   →   {target.Name}"

        for zone in settings.Placement.Zones do
            caption $"{prefixText settings.Placement.Prefix}+{keyName zone.Key}   →   {zone.Name}"

        match settings.Placement.NextMonitorVk with
        | Some vk -> caption $"{prefixText settings.Placement.Prefix}+{keyName vk}   →   Next monitor"
        | None -> ()

        match settings.Placement.CycleDisplayVk with
        | Some vk -> caption $"{prefixText settings.Placement.Prefix}+{keyName vk}   →   Cycle display"
        | None -> ()

    menu.Items.Add(new ToolStripSeparator()) |> ignore

    let edit = new ToolStripMenuItem("Edit config...")

    edit.Click.Add(fun _ ->
        try
            Process.Start(ProcessStartInfo(Config.file, UseShellExecute = true)) |> ignore
        with error ->
            Log.write "edit config" error)

    menu.Items.Add(edit) |> ignore

    let reload = new ToolStripMenuItem("Reload config")
    reload.Click.Add(fun _ -> onReload ())
    menu.Items.Add(reload) |> ignore

    menu.Items.Add(new ToolStripSeparator()) |> ignore

    let quit = new ToolStripMenuItem("Exit")
    quit.Click.Add(fun _ -> Application.ExitThread())
    menu.Items.Add(quit) |> ignore
    menu

/// NotifyIcon.Text throws above 63 characters, so this has to stay short.
let private trayText () =
    match Config.current with
    | None -> "Mashed Potato - no configuration"
    | Some settings ->
        let bindings =
            settings.Launcher.Apps
            |> List.map (fun (vk, target) -> $"{keyName vk}={target.Name}")
            |> String.concat " "

        let text = $"Mashed Potato - {prefixText settings.Launcher.Prefix} then {bindings}"
        if text.Length > 63 then text.Substring(0, 63) else text

let run () =
    let configProblem = Config.load ()
    Application.EnableVisualStyles()

    // Windows calls a WH_KEYBOARD_LL hook on the thread that installed it, so the
    // callback already runs here. This hidden window is what defers the actual work
    // out of the callback, which has to return promptly or Windows drops the hook.
    // Putting the banner up is cheap enough to do inline.
    use pump = new Form()

    // Touching .Handle forces WinForms to create the underlying HWND now. Until
    // something asks, it defers creation - and a window with no handle cannot be
    // posted to.
    pump.Handle |> ignore

    // BeginInvoke posts a delegate to the UI thread's message queue and returns
    // immediately; the message loop runs it on the next turn. It is the standard
    // .NET answer to "I am not allowed to touch the UI from here" - and also, as
    // here, to "I must return from this callback right now".
    //
    // Action is a BCL delegate type meaning `unit -> unit`. Delegates are .NET's
    // named function types; F# functions convert to them, but not always
    // implicitly, hence the explicit construction.
    let onLoop (work: unit -> unit) = pump.BeginInvoke(Action(work)) |> ignore

    use _banner = Overlay.create ()

    // The banner is raised and lowered through the message loop, not inside the
    // hook callback. Touching WinForms from the callback means re-entering the UI
    // while Windows is part-way through delivering a keystroke; posting keeps the
    // callback to bookkeeping, which is all it should ever have been doing.
    let handlers : Chord.Handlers =
        { Toggle = fun target -> onLoop (fun () -> App.activate target)
          Prompt = fun visible -> onLoop (fun () -> Overlay.setVisible visible)
          SnapTo = fun zone -> onLoop (fun () -> Snap.toZone zone)
          NextMonitor = fun () -> onLoop Snap.toNextMonitor
          CycleDisplay = fun () -> onLoop Display.cycle }

    match Chord.install handlers with
    | Error code ->
        MessageBox.Show(
            $"Could not install the keyboard hook (error {code}).",
            "Mashed Potato",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error)
        |> ignore
        1
    | Ok() ->
        use icon = trayIcon ()
        use tray = new NotifyIcon(Icon = icon, Visible = true)

        let balloon icon message =
            tray.BalloonTipTitle <- "Mashed Potato"
            tray.BalloonTipText <- message
            tray.BalloonTipIcon <- icon
            tray.ShowBalloonTip(5000)

        // The menu closes over `reload`, and reload rebuilds the menu.
        let rec refresh () =
            let previous = tray.ContextMenuStrip
            tray.ContextMenuStrip <- buildTrayMenu reload
            tray.Text <- trayText ()
            if not (isNull previous) then previous.Dispose()

        and reload () =
            match Config.load () with
            | Some problem -> balloon ToolTipIcon.Error problem
            | None -> balloon ToolTipIcon.Info "Config reloaded."

            refresh ()

        refresh ()

        // Nothing to disambiguate a double-click, so it takes the first app - if
        // the file lists any.
        tray.DoubleClick.Add(fun _ ->
            match Config.current |> Option.bind (fun settings -> List.tryHead settings.Launcher.Apps) with
            | Some(_, target) -> App.activate target
            | None -> ())

        App.onError <-
            fun message ->
                Log.write "app" (Exception message)
                balloon ToolTipIcon.Error message

        match configProblem with
        | Some problem -> balloon ToolTipIcon.Error problem
        | None -> ()

        try
            // The message loop. This blocks until Application.ExitThread is called,
            // pulling messages off this thread's queue and dispatching them - which
            // is what makes the tray icon respond, the banner paint, BeginInvoke
            // work, and the keyboard hook fire at all.
            Application.Run()
            0
        finally
            Chord.uninstall ()
            tray.Visible <- false

/// [<EntryPoint>] marks the program's start; the int it returns is the process exit
/// code. [<STAThread>] sets this thread's COM apartment to "single-threaded", which
/// sounds arcane but is required by anything that touches the Windows shell - drag
/// and drop, common dialogs, ShellExecute. WinForms apps are always STA.
