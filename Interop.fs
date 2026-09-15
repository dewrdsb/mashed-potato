// 1 of 12 - the Windows API
//
// ---------------------------------------------------------------------------------
// A primer, if you know F# but not .NET or Windows
// ---------------------------------------------------------------------------------
//
// WINDOWS, IN FIVE IDEAS
//
// 1. Everything on screen is a *window*, identified by an HWND - an opaque integer
//    handle. Not just top-level windows: a button is a window, a text box is a
//    window. You never see inside an HWND; you pass it back to the API.
//
// 2. Windows are driven by *messages*. The OS puts messages (WM_KEYDOWN, WM_PAINT,
//    WM_CLOSE...) on a per-thread queue, and the thread runs a *message loop* that
//    pulls them off and dispatches each to the right window's handler. A GUI thread
//    is a loop; if it stops looping, its windows freeze. `Application.Run()` at the
//    bottom of this file is that loop.
//
// 3. A window belongs to the thread that created it. That thread is the only one
//    allowed to touch it. This is why work gets *posted* to the UI thread rather
//    than done wherever it arises - `pump.BeginInvoke` below.
//
// 4. A *hook* lets you intercept messages destined for other programs. This app is
//    built on WH_KEYBOARD_LL, a keyboard hook that sees every keystroke before the
//    focused application does, and can swallow it.
//
// 5. Exactly one window at a time is the *foreground* window - the one receiving
//    keystrokes. Windows heavily restricts who may change it, which is why focusing
//    an app takes more than one call here.
//
// .NET, IN FOUR IDEAS
//
// 1. The Base Class Library (BCL) is the standard library: `System.IO`,
//    `System.Diagnostics`, `System.Text.Json` and so on. The `open` list above is
//    all BCL except for our own code.
//
// 2. WinForms (`System.Windows.Forms`) is an old but built-in GUI library that wraps
//    the Windows API above: `Form` wraps a window, `Label` wraps a child window,
//    `NotifyIcon` wraps a tray icon. It is used here because it ships with .NET and
//    needs no dependencies.
//
// 3. P/Invoke ("platform invoke") calls C functions in DLLs directly. The wall of
//    `extern` declarations in the Interop module below is the Windows API, declared
//    so .NET can call it. The runtime *marshals* arguments - converts .NET values
//    into the C representations the DLL expects.
//
// 4. `IDisposable` is .NET's "this holds an OS resource, release it deterministically"
//    interface - F#'s `use` binding calls `Dispose` at end of scope. Windows objects
//    like fonts, windows and process handles are all disposable. Garbage collection
//    handles memory; it does not promptly handle OS handles.

//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

module MashedPotato.Interop

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Windows.Forms

// ---- types the Windows API expects ------------------------------------------
//
// A .NET delegate handed to a C API becomes a plain function pointer. Windows
// will call back through it on our thread. The signature has to match what
// Windows declares for a keyboard hook procedure, argument for argument.
type LowLevelKeyboardProc = delegate of int * nativeint * nativeint -> nativeint

// StructLayout(Sequential) tells the runtime to lay these fields out in memory in
// declaration order with C's alignment rules, so the bytes match what the C struct
// looks like. Without it .NET is free to reorder fields and the interop breaks.
//
// This is the struct Windows passes with every keystroke; `vkCode` is a *virtual
// key code*, Windows' layout-independent key identifier (0x41 is "the A key",
// whatever is printed on it). System.Windows.Forms.Keys is an enum of them.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type KBDLLHOOKSTRUCT =
    val vkCode : uint32
    val scanCode : uint32
    val flags : uint32
    val time : uint32
    val dwExtraInfo : nativeint

// Windows' rectangle is two corners, not a corner plus a size - unlike .NET's
// System.Drawing.Rectangle, which this file converts to as soon as it can.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type RECT =
    val Left : int
    val Top : int
    val Right : int
    val Bottom : int

    // Windows fills these in; this is for the one place that has to hand one back.
    new(left, top, right, bottom) = { Left = left; Top = top; Right = right; Bottom = bottom }

[<Struct; StructLayout(LayoutKind.Sequential)>]
type POINT =
    val x : int
    val y : int

/// What ShowWindow state a window is in, plus - the reason this is here at all -
/// where it will come back to when it is restored. A minimized window's rect is
/// (-32000, -32000): Windows parks it off-screen, and rcNormalPosition is the only
/// record of where it belongs.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type WINDOWPLACEMENT =
    val mutable length : uint32
    val mutable flags : uint32
    val mutable showCmd : uint32
    val mutable ptMinPosition : POINT
    val mutable ptMaxPosition : POINT
    val mutable rcNormalPosition : RECT

// ---- constants ---------------------------------------------------------------
//
// The Windows API is C, so these arrive as bare integers with conventional name
// prefixes. Knowing the prefixes makes the API browsable:
//
//   WM_   window message          WH_   hook type
//   SW_   ShowWindow command      SWP_  SetWindowPos flag
//   GW_   GetWindow relationship  WS_EX_ extended window style
//
// [<Literal>] makes an F# binding a true compile-time constant, which is what
// lets these be used in pattern matches and attributes.

[<Literal>]
let WH_KEYBOARD_LL = 13 // the "low-level keyboard" hook: every key, system wide
[<Literal>]
let HC_ACTION = 0 // hook code meaning "this is a real event, look at it"

// Alt makes Windows send SYSKEYDOWN rather than KEYDOWN - a hangover from menu
// handling. Any hotkey involving Alt has to accept both.
[<Literal>]
let WM_KEYDOWN = 0x0100
[<Literal>]
let WM_SYSKEYDOWN = 0x0104
/// Broadcast to every top-level window when the desktop's shape changes - a monitor
/// arriving or leaving, a resolution change, a topology switch.
[<Literal>]
let WM_DISPLAYCHANGE = 0x007E
/// Broadcast when the device tree changes. DBT_DEVNODES_CHANGED needs no
/// registration, unlike the device-interface notifications, and says only "something
/// changed" - which is all that is wanted here, since the answer is recomputed
/// from scratch anyway.
[<Literal>]
let WM_DEVICECHANGE = 0x0219
[<Literal>]
let DBT_DEVNODES_CHANGED = 0x0007

/// Set on keystrokes generated by software (SendInput) rather than by a keyboard.
[<Literal>]
let LLKHF_INJECTED = 0x10u

// Z-order is the front-to-back stacking of windows. GetWindow walks relationships
// between windows; HWNDNEXT steps one place further back, OWNER asks which window
// a window belongs to (dialogs are "owned" by the window that opened them).
[<Literal>]
let GW_HWNDNEXT = 2
[<Literal>]
let GW_OWNER = 4

// ShowWindow commands. "Iconic" is the old Windows word for minimized, and
// "zoomed" for maximized, which is why the test functions are named as they are.
[<Literal>]
let SW_SHOW = 5
[<Literal>]
let SW_MAXIMIZE = 3
[<Literal>]
let SW_MINIMIZE = 6
[<Literal>]
let SW_RESTORE = 9

// Extended window styles, set when a window is created. TOOLWINDOW keeps a window
// out of Alt+Tab; NOACTIVATE means clicking or showing it never gives it focus.
[<Literal>]
let WS_EX_TOOLWINDOW = 0x00000080
[<Literal>]
let WS_EX_NOACTIVATE = 0x08000000

// SetWindowPos does several jobs at once; the flags say which parts to skip.
[<Literal>]
let SWP_NOZORDER = 0x0004u
[<Literal>]
let SWP_NOACTIVATE = 0x0010u

/// DWM is the Desktop Window Manager, the compositor that has drawn and animated
/// windows since Vista. It knows where a window *visually* ends, which is not the
/// same as where the window officially is - see Snap.allowingForBorder.
[<Literal>]
let DWMWA_EXTENDED_FRAME_BOUNDS = 9

[<Literal>]
let MAPVK_VK_TO_CHAR = 2u

/// The least privileged way to open a process handle - enough to ask its name,
/// not enough to read its memory. Asking for more can be refused.
[<Literal>]
let PROCESS_QUERY_LIMITED_INFORMATION = 0x1000u

// SetDisplayConfig's flags. SDC_APPLY means "do it", and exactly one SDC_TOPOLOGY_*
// says which arrangement to put on. Windows remembers a monitor layout per topology,
// so naming one is enough - there is no need to describe the displays.
[<Literal>]
let SDC_TOPOLOGY_INTERNAL = 0x00000001u
[<Literal>]
let SDC_TOPOLOGY_CLONE = 0x00000002u
[<Literal>]
let SDC_TOPOLOGY_EXTEND = 0x00000004u
[<Literal>]
let SDC_TOPOLOGY_EXTERNAL = 0x00000008u
[<Literal>]
let SDC_APPLY = 0x00000080u

// ---- the Windows API itself --------------------------------------------------
//
// Each of these is a C function living in a Windows DLL. [<DllImport>] names the
// DLL; `extern` declares the signature; the runtime finds the export and marshals
// arguments across on every call. Get a signature wrong and nothing complains at
// compile time - it corrupts the stack at run time, so these are copied carefully
// from the documentation.
//
// The three DLLs here are the classic division of the Windows API:
//   user32.dll   windows, input, the desktop
//   kernel32.dll processes, handles, the basics below the GUI
//   dwmapi.dll   the compositor
//
// Conventions worth knowing:
//   nativeint     an opaque OS handle (HWND, HHOOK, process handle). .NET calls
//                 this IntPtr; it is pointer-sized and means "a number Windows
//                 gave me". 0n is the null handle.
//   RECT&         F# byref, which is C's "pointer to a struct I will fill in" -
//                 the ubiquitous Windows out-parameter. Called as `&myRect`.
//   SetLastError  Windows reports failure via a thread-local error code; this
//                 tells .NET to capture it so Marshal.GetLastWin32Error works.
//   CharSet       Windows exports two versions of any function taking text -
//                 FooA (8-bit) and FooW (UTF-16). Unicode picks W, Auto picks W
//                 on anything modern.

// Hooks. SetWindowsHookEx installs the callback, CallNextHookEx passes an event
// along to whoever hooked before us - not calling it is how an event is swallowed.
[<DllImport("user32.dll", SetLastError = true)>]
extern nativeint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nativeint hMod, uint32 dwThreadId)
[<DllImport("user32.dll")>]
extern bool UnhookWindowsHookEx(nativeint hhk)
[<DllImport("user32.dll")>]
extern nativeint CallNextHookEx(nativeint hhk, int nCode, nativeint wParam, nativeint lParam)

/// Maps a character to a virtual key code using the *current keyboard layout*.
/// The low byte is the key; the high byte is the modifiers needed to type the
/// character. Returns -1 if the character cannot be typed on this layout.
[<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
extern int16 VkKeyScan(char ch)

/// Translates between virtual keys, scan codes and characters. With
/// MAPVK_VK_TO_CHAR it gives the unshifted character a key produces, which is
/// what lets a menu say "[" instead of "OemOpenBrackets".
[<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
extern uint32 MapVirtualKey(uint32 code, uint32 mapType)

/// Asks whether a key is physically down *right now*, rather than waiting for its
/// message. The answer is in the high bit of the result - see isHeld below.
[<DllImport("user32.dll")>]
extern int16 GetAsyncKeyState(int vKey)

// Walking the desktop. GetTopWindow(0n) means "the frontmost child of the desktop",
// i.e. the frontmost top-level window; GetWindow with GW_HWNDNEXT steps backwards
// through the Z-order from there.
[<DllImport("user32.dll")>]
extern nativeint GetTopWindow(nativeint hwnd)
[<DllImport("user32.dll")>]
extern nativeint GetWindow(nativeint hwnd, int cmd)

// Asking a window about itself. A window can be visible, minimized ("iconic") or
// maximized ("zoomed"); a minimized window is still visible in this sense.
[<DllImport("user32.dll")>]
extern bool IsWindowVisible(nativeint hwnd)
[<DllImport("user32.dll")>]
extern bool IsIconic(nativeint hwnd)
[<DllImport("user32.dll")>]
extern bool IsZoomed(nativeint hwnd)
[<DllImport("user32.dll")>]
extern int GetWindowTextLength(nativeint hwnd)
[<DllImport("user32.dll")>]
extern bool GetWindowRect(nativeint hwnd, RECT& rect)

/// Where DWM thinks the window visually is, which excludes the invisible resize
/// border GetWindowRect includes.
[<DllImport("dwmapi.dll")>]
extern int DwmGetWindowAttribute(nativeint hwnd, int attribute, RECT& value, int size)

/// Reading and writing a window's restore position. `length` must be filled in
/// before either call - it is how the API knows which version of the struct it has.
/// Writing one back preserves showCmd, so a minimized window stays minimized.
[<DllImport("user32.dll")>]
extern bool GetWindowPlacement(nativeint hwnd, WINDOWPLACEMENT& placement)

[<DllImport("user32.dll")>]
extern bool SetWindowPlacement(nativeint hwnd, WINDOWPLACEMENT& placement)

// Moving and showing windows. SetWindowPos moves and resizes in one call.
[<DllImport("user32.dll")>]
extern bool ShowWindow(nativeint hwnd, int cmd)
[<DllImport("user32.dll")>]
extern bool SetWindowPos(nativeint hwnd, nativeint insertAfter, int x, int y, int cx, int cy, uint32 flags)
[<DllImport("user32.dll")>]
extern bool BringWindowToTop(nativeint hwnd)

/// A rounded-rectangle region. The last two arguments are the *diameters* of the
/// ellipse used for the corners, not the radii. gdi32 is the old drawing API, below
/// GDI+ and WinForms; regions are one of the few parts of it still worth reaching for.
[<DllImport("gdi32.dll")>]
extern nativeint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse)

[<DllImport("gdi32.dll")>]
extern bool DeleteObject(nativeint handle)

/// Restricts a window to a region: anything outside it is not drawn and not clicked,
/// child controls included. Non-zero is success.
///
/// The ownership rule is unusual and worth stating. On success the *system* takes
/// the region handle - it must not be deleted, reused, or passed to SetWindowRgn
/// again, and the previously set region is freed by Windows. On failure it is still
/// ours, and leaking it leaks a GDI object.
[<DllImport("user32.dll")>]
extern int SetWindowRgn(nativeint hwnd, nativeint region, bool redraw)

/// Changes which monitors make up the desktop. Unlike most of user32 it returns a
/// Win32 error code directly rather than a bool - zero is success. With null path
/// and mode arrays it applies a remembered topology; that is the whole of what
/// Display.cycle needs, so the two array parameters are always zero here.
[<DllImport("user32.dll")>]
extern int SetDisplayConfig(uint32 numPathArrayElements, nativeint pathArray, uint32 numModeInfoArrayElements, nativeint modeInfoArray, uint32 flags)

// The foreground window - the one that receives keystrokes. SetForegroundWindow
// is allowed to fail: Windows only lets a process take focus if it has had recent
// user input, to stop programs stealing it. App.focus works around that.
[<DllImport("user32.dll")>]
extern bool SetForegroundWindow(nativeint hwnd)
[<DllImport("user32.dll")>]
extern nativeint GetForegroundWindow()

/// The desktop window - "Program Manager", class Progman, owned by explorer.exe.
/// It is visible, unowned and titled, so it passes every test a real window does
/// and has to be excluded by identity.
[<DllImport("user32.dll")>]
extern nativeint GetShellWindow()

/// Which thread (return value) and process (out parameter) own a window. Two
/// answers from one call, which is why it looks odd.
[<DllImport("user32.dll")>]
extern uint32 GetWindowThreadProcessId(nativeint hwnd, uint32& processId)

/// Temporarily joins two threads' input queues, so they share focus state. This
/// is the standard trick for getting round the foreground restriction.
[<DllImport("user32.dll")>]
extern bool AttachThreadInput(uint32 idAttach, uint32 idAttachTo, bool attach)

[<DllImport("kernel32.dll")>]
extern uint32 GetCurrentThreadId()

// Process handles are OS resources: opened, used, and closed. Forgetting
// CloseHandle leaks a kernel object, which the garbage collector cannot help with.
[<DllImport("kernel32.dll")>]
extern nativeint OpenProcess(uint32 access, bool inheritHandle, int processId)
[<DllImport("kernel32.dll")>]
extern bool CloseHandle(nativeint handle)

/// The classic Win32 string-out pattern: the caller supplies a buffer and its
/// size, the API fills it. A .NET StringBuilder marshals as exactly that buffer.
[<DllImport("kernel32.dll", CharSet = CharSet.Unicode)>]
extern bool QueryFullProcessImageName(nativeint handle, uint32 flags, StringBuilder buffer, int& size)

/// The base address of a loaded module. GetModuleHandle(null) means "this .exe",
/// which is the module handle SetWindowsHookEx wants for a low-level hook.
[<DllImport("kernel32.dll", CharSet = CharSet.Auto)>]
extern nativeint GetModuleHandle(string moduleName)

/// The process that owns a window, or 0 if there is no such window.
let processIdOf (hwnd: nativeint) =
    if hwnd = 0n then
        0
    else
        let mutable pid = 0u
        GetWindowThreadProcessId(hwnd, &pid) |> ignore
        int pid

/// The image name (without extension) of the process that owns a window.
///
/// Note the shape: open a handle, use it, close it in a `finally`. Windows handles
/// are not garbage collected, and `try/finally` is how .NET guarantees the close
/// even if something throws in between.
let processNameOf (hwnd: nativeint) =
    match processIdOf hwnd with
    | 0 -> None
    | pid ->
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid)
        if handle = 0n then
            None
        else
            try
                let buffer = StringBuilder(1024)
                let mutable size = buffer.Capacity
                if QueryFullProcessImageName(handle, 0u, buffer, &size) then
                    Some(Path.GetFileNameWithoutExtension(buffer.ToString()))
                else
                    None
            finally
                CloseHandle(handle) |> ignore

/// Every top-level window, topmost first. GetTopWindow plus GW_HWNDNEXT is the
/// documented way to walk the Z-order; EnumWindows merely happens to come back
/// in that order.
let topLevelWindows () =
    let rec walk hwnd acc =
        if hwnd = 0n then List.rev acc else walk (GetWindow(hwnd, GW_HWNDNEXT)) (hwnd :: acc)

    walk (GetTopWindow(0n)) []

/// GetAsyncKeyState packs two answers into one integer: the high bit (0x8000) is
/// "down right now", the low bit is "pressed since last asked". Only the high bit
/// is wanted here. Keys is the WinForms enum of virtual key codes.
let isHeld (key: Keys) =
    int (GetAsyncKeyState(int key)) &&& 0x8000 <> 0

// ---- what is plugged in ------------------------------------------------------
//
// Two questions a layout has to answer before it can place anything: which monitors
// are attached, and which dock the machine is sitting on. Windows keeps the answers
// in two quite different places.
//
// Monitors come from the display driver, through EnumDisplayDevices. It is called
// twice with different arguments: pass null and an index to walk the *adapters*
// (\\.\DISPLAY1, \\.\DISPLAY2...), pass one of those names and index 0 to get the
// monitor attached to it - the friendly name off the EDID, "Dell P2222H (DP)".
// Screen.DeviceName from WinForms is the adapter name, which is the join between
// the two APIs.
//
// The dock comes from the device tree, through SetupAPI - the same enumeration
// Device Manager shows. There is no "am I docked" call: a dock is simply a device
// that is present, so the test is whether anything present is named like one.

[<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
type DISPLAY_DEVICE =
    val mutable cb : int
    [<MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)>]
    val mutable DeviceName : string
    [<MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)>]
    val mutable DeviceString : string
    val mutable StateFlags : int
    [<MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)>]
    val mutable DeviceID : string
    [<MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)>]
    val mutable DeviceKey : string

/// SetupAPI's handle onto one device in the tree. cbSize has to be filled in before
/// every call - it is how the API knows which version of the struct it was handed.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type SP_DEVINFO_DATA =
    val mutable cbSize : uint32
    val mutable ClassGuid : Guid
    val mutable DevInst : uint32
    val mutable Reserved : nativeint

[<Literal>]
let DIGCF_PRESENT = 0x00000002u // only devices actually plugged in right now
[<Literal>]
let DIGCF_ALLCLASSES = 0x00000004u // every class, rather than one interface
[<Literal>]
let SPDRP_DEVICEDESC = 0x00000000u // the INF's name for the device
[<Literal>]
let SPDRP_FRIENDLYNAME = 0x0000000Cu // the name it calls itself, when it has one

[<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
extern bool EnumDisplayDevices(string device, uint32 index, DISPLAY_DEVICE& info, uint32 flags)

[<DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
extern nativeint SetupDiGetClassDevs(nativeint classGuid, string enumerator, nativeint parent, uint32 flags)

[<DllImport("setupapi.dll", SetLastError = true)>]
extern bool SetupDiEnumDeviceInfo(nativeint devices, uint32 index, SP_DEVINFO_DATA& info)

[<DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
extern bool SetupDiGetDeviceRegistryProperty(nativeint devices, SP_DEVINFO_DATA& info, uint32 property, uint32& regType, byte[] buffer, uint32 size, uint32& required)

[<DllImport("setupapi.dll")>]
extern bool SetupDiDestroyDeviceInfoList(nativeint devices)

/// The monitor attached to an adapter, by its EDID name - "Dell P2222H (DP)",
/// "Integrated Monitor". None when the adapter has nothing on it.
let monitorNameOf (adapter: string) =
    let mutable info = DISPLAY_DEVICE()
    info.cb <- Marshal.SizeOf(typeof<DISPLAY_DEVICE>)

    if EnumDisplayDevices(adapter, 0u, &info, 0u) && not (String.IsNullOrWhiteSpace info.DeviceString) then
        Some(info.DeviceString.Trim())
    else
        None

/// The description of every device present, which is how a dock is recognised:
/// "Dell Pro Dock WD25" is in this list while docked and gone once unplugged.
///
/// This walks the whole device tree - a couple of thousand entries on a laptop -
/// so it is called once when a layout is applied, never from the keyboard hook.
let presentDeviceNames () =
    let devices = SetupDiGetClassDevs(0n, null, 0n, DIGCF_PRESENT ||| DIGCF_ALLCLASSES)

    // INVALID_HANDLE_VALUE, which is -1 rather than 0 - a SetupAPI habit.
    if devices = -1n || devices = 0n then
        []
    else
        try
            let buffer = Array.zeroCreate<byte> 1024

            let rec walk index found =
                let mutable info = SP_DEVINFO_DATA()
                info.cbSize <- uint32 (Marshal.SizeOf(typeof<SP_DEVINFO_DATA>))

                if not (SetupDiEnumDeviceInfo(devices, index, &info)) then
                    found
                else
                    let mutable regType = 0u
                    let mutable required = 0u

                    // `required` counts the trailing NUL, two bytes of it. A value
                    // longer than the buffer fails the call rather than truncating;
                    // nothing that long is a dock, so it is skipped.
                    let text () = Encoding.Unicode.GetString(buffer, 0, max 0 (int required - 2))

                    // Friendly name first, description second - which is the order
                    // Device Manager and WMI resolve a device's name in, and it
                    // matters here: the WD25 describes itself as "WinUsb Device"
                    // and is only called "Dell Pro Dock WD25" by its friendly name.
                    //
                    // Written out twice rather than through a helper because `info`
                    // is a mutable local, and F# will not let a closure capture one.
                    let friendly =
                        if SetupDiGetDeviceRegistryProperty(
                            devices, &info, SPDRP_FRIENDLYNAME, &regType, buffer, uint32 buffer.Length, &required) then
                            text ()
                        else
                            ""

                    let name =
                        if friendly <> "" then
                            friendly
                        elif SetupDiGetDeviceRegistryProperty(
                            devices, &info, SPDRP_DEVICEDESC, &regType, buffer, uint32 buffer.Length, &required) then
                            text ()
                        else
                            ""

                    walk (index + 1u) (if name = "" then found else name :: found)

            walk 0u []
        finally
            SetupDiDestroyDeviceInfoList(devices) |> ignore
