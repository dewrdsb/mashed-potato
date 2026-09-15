# Mashed Potato

<img src=icon/artwork.jpg width=480px/>

Mashed Potato is a keyboard driven daemon that provides two specific features:

1. App launcher, switcher, toggler.
2. Window placer.

## Configuration

Everything lives in

    %APPDATA%\Mashed Potato\mashedpotato.json

**There are no defaults in the code.** If that file cannot be read, nothing is
bound and a tray balloon says why. On a first run the copy shipped beside the
executable is installed there to be edited - a deployment default, not a code one.
Editing is **Edit config...** then **Reload config** from the tray; no rebuild and
no restart.

Two sections, each with its own prefix:

```json
{
  "launcher": {
    "prefix": "Ctrl+K",
    "passThroughIn": [],
    "apps": [
      { "key": "Y", "name": "Spotify", "processName": "Spotify",
        "paths": ["%APPDATA%\\Spotify\\Spotify.exe"], "shellFallback": "spotify:" }
    ]
  },
  "placement": {
    "prefix": "Ctrl+Shift+Alt",
    "nextMonitor": "Z",
    "cycleDisplay": "1",
    "zones": [
      { "key": "H", "name": "Left two thirds", "side": "left", "width": "2/3", "height": "1" }
    ]
  }
}
```

### Prefixes

A prefix may be written either way:

```json
"prefix": "Ctrl+K"
"prefix": { "key1": "Ctrl", "key2": "K" }
```

The object form is ordered by the number in each property name, so `key10` follows
`key9` rather than `key1`. Modifiers are `Ctrl`, `Shift`, `Alt`, `Win` (`Control`,
`Menu`, `Windows` and `Super` are accepted too).

The two sections take different *shapes*, and the difference is enforced:

| section | shape | means |
|---|---|---|
| `launcher` | modifiers **and** a key | a chord: press and release `Ctrl+K`, then a key picks the app |
| `placement` | modifiers **only** | held down while the zone key is pressed |

A third section, `layouts`, has no prefix of its own: it is reached by one key under
the `placement` prefix. See [Layouts](#layouts).

Modifiers must match **exactly**. A `Ctrl` prefix does not fire while Ctrl+Shift is
held, so two sections can never shadow each other unless they are given the same
modifier set - in which case `placement` is tested first.

### Fields

| Field | Meaning |
|---|---|
| `launcher.passThroughIn` | process names (no `.exe`) that keep their own prefix key |
| `placement.nextMonitor` | key that sends the window to the next monitor; omit, or `"none"`, to leave it unbound |
| `placement.cycleDisplay` | key that switches the desktop to internal-only and back to extend; omit, or `"none"`, to leave it unbound |
| `placement.applyLayout` | key that arranges every window at once; omit, or `"none"`, to leave it unbound. Needs a `layouts` section, and vice versa |
| `placement.applyLayoutOnChange` | seconds to wait after the hardware settles before applying the layout that now fits; `"off"` or omitted leaves the key as the only trigger |
| `layouts[].when` | `dock`, `monitors`, both, or omitted entirely for a catch-all. First profile that matches wins |
| `layouts[].windows[].app` | the `name` of one of the launcher's apps - the two sections share a definition rather than repeating one |
| `monitor` | a number from 1, `"primary"`, or part of a monitor's name; the tray menu lists what is attached |
| `rect` | `"left, top, width, height"` as fractions of the monitor - `"2/3, 0, 1/3, 1"` is the right third, full height. **Omit it** to mean "that monitor, wherever on it it already was" |
| `instances` | `"all"` for every window the app has open; omit, or `"front"`, for just the one `Ctrl+K` would switch to |
| `key` | the character the key types - `"Y"`, `"["`, `";"`, `"4"` - or a `Keys` name for keys that type nothing: `"F1"`, `"Escape"`, `"NumPad7"`, `"Space"` |
| `processName` | the **image name**, as shown in Task Manager's *Details* tab - not the app's display name. VS Code is `Code`, not `Visual Studio Code`. A `.exe` suffix is stripped for you |
| `paths` | launch candidates, first one that exists wins; `%VARS%` are expanded |
| `shellFallback` | used when no path exists - a URI scheme, or a bare exe name |
| `side` | `left`, `right`, `top`, `bottom`, `topleft`, `topright`, `bottomleft`, `bottomright`, `center` |
| `width` / `height` | a fraction of the monitor on that axis - `1`, `1/2`, `2/3`; omit for the whole axis |

A side names an edge, a corner, or the centre. Whichever axis it does not pin is
centred - so `"side": "left", "width": "1/4", "height": "1/2"` is a quarter-wide
block against the left edge, vertically centred, while `topleft` pins both. Spaces
and hyphens in a side are ignored, so `bottom-left` reads the same as `bottomleft`.

Comments and trailing commas are allowed in the file.

### When the file is wrong

Parsing is all or nothing, and every failure names what it choked on:

```
launcher prefix: this prefix needs a key as well as modifiers, as in Ctrl+K
placement prefix: this prefix is held down, so it must be modifiers only - remove Q
a placement section is required
zone 'Top Right': ']' is not a key - use the character it types, or a Keys name...
app 'VS Code': processName is required - it is the image name shown in Task Manager's...
```

At startup a bad file means nothing is bound. A bad **reload** keeps the previous
configuration in force rather than unbinding everything mid-session. Either way the
message goes to a tray balloon and to `mashedpotato.log`.

### It reloads itself when you save

Saving the file reloads it. There is nothing to turn on and no reason to reach for
the tray menu, which is still there for when you want to be told it worked.

Three things stand between a file changing and a reload, and each is a way this goes
wrong if left out.

**Changed is not enough.** A great many editors - VS Code, vim, Notepad since
Windows 10 - never write to the file at all. They write a temporary one beside it
and rename it over the top, which is what makes a save atomic and what makes a
watcher listening only for `Changed` miss the save completely. `Created` and
`Renamed` are watched for that reason. `Deleted` deliberately is not: it is mostly
the first half of one of those renames, and reacting to it would mean reading a file
that is about to exist again - or worse, `load` reinstalling the shipped copy over an
edit in progress.

**One save is not one event.** Writing a file is several operations and arrives as
two or three notifications. The timer is restarted rather than started on each, the
same way the hardware watcher settles, so a save collapses into one reload.

**The writer may not have let go.** The event can arrive while the editor still
holds the handle, and a read then either fails or returns half a file. Neither is a
broken configuration and neither should be reported as one, so the timer re-arms and
tries again - four times, and then once anyway, because a lock that never clears is
worth complaining about.

An automatic reload is **silent when it works** and loud when it does not. Choosing
*Reload config* from the tray is a question and gets an answer; a save is not, and a
balloon on every one of them while you are editing zones would be its own small
punishment. A file that will not load interrupts either way.

The one thing not configurable is what an unbound key does while the banner is
up: `Escape`, the prefix again and any other unbound key all dismiss it, and
none of them reach the window underneath.

> **A note on the executable name.** The binary is `Mashed.exe`, not
> `MashedPotato.exe`. Endpoint security on this machine refuses to launch any
> executable whose filename ends in `potato.exe` - a rule aimed at Potato Chat,
> whose binary is `Potato.exe`. Identical bytes run happily under any other name.
> Only the filename is affected: the namespace is `MashedPotato`, the config lives
> in `%APPDATA%\Mashed Potato\`, and the tray says Mashed Potato.

## Build and run

    ./build.sh
    cmd.exe /c start "" "%LOCALAPPDATA%\Programs\Mashed Potato\Mashed.exe"

Needs the .NET SDK - F# has no in-box compiler the way C# does. Targets
`net10.0-windows` framework-dependent, so the .NET Desktop Runtime is needed at
run time (both are already installed here). Sources are staged under
`%LOCALAPPDATA%` before building only because `dotnet.exe` refuses to run with a
`\\wsl.localhost` working directory.

### Where things live

Windows draws a firm line between data that *roams* and data that does not, and on
a domain account the difference is real: `%APPDATA%` is copied over the network at
every logon and logoff, `%LOCALAPPDATA%` is not.

| | Path | Why |
|---|---|---|
| Install | `%LOCALAPPDATA%\Programs\Mashed Potato\` | a per-user install needs no administrator; this is where VS Code and Teams put theirs |
| Config | `%APPDATA%\Mashed Potato\mashedpotato.json` | settings are small and precious, and you want them on every machine you sign in to |
| Log | `%LOCALAPPDATA%\Mashed Potato\mashedpotato.log` | diagnostics are machine-specific and grow without bound; a log in a roaming profile is copied across the network at every sign-in |
| Build scratch | `%LOCALAPPDATA%\Mashed Potato\build\` | derived, disposable, never roams |

The other two worth knowing: `%PROGRAMDATA%` for data shared by every user on the
machine, and `%TEMP%` for anything genuinely throwaway. An installer that serves all
users writes to `%PROGRAMFILES%` instead, and needs administrator rights to do it.

### Start it at login

`Win+R`, run `shell:startup`, drop a shortcut to `Mashed.exe` in the folder
that opens.

## Layout

One module per file, listed in `MashedPotato.fsproj` in dependency order. F# compiles
files in that sequence and a file may only use what earlier ones declare, so the
list *is* the dependency graph - and a cycle is a compile error rather than a
design review.

| # | File | Role |
|---|------|------|
| 1 | `Interop.fs` | the Windows API: P/Invoke declarations and thin helpers |
| 2 | `Log.fs` | `mashedpotato.log`, because a tray daemon fails invisibly |
| 3 | `Domain.fs` | pure data - no Windows, no JSON, no behaviour |
| 4 | `Config.fs` | reading `mashedpotato.json` into those types, and noticing when it changes |
| 5 | `App.fs` | launch / focus / minimize an application |
| 6 | `Snap.fs` | move and resize the current window |
| 7 | `Display.fs` | cycling the desktop topology |
| 8 | `Layout.fs` | arranging every window at once |
| 9 | `Chord.fs` | the keyboard hook and what a key means |
| 10 | `Overlay.fs` | the on-screen banner |
| 11 | `Daemon.fs` | tray icon, message pump, assembly |
| 12 | `Program.fs` | entry point |

Two files beside them are not code: `mashed.ico`, and `icon/` holding the artwork it
was made from together with the script that makes it. See [The icon](#the-icon).

Each file declares a top-level module - `module MashedPotato.Snap` - rather than a
namespace with a module nested inside it. Both are idiomatic; the top-level form
puts the whole file at column 0 instead of indenting everything by four, which is
worth having when the file *is* the module.

`Domain.fs` is the exception, and is a `namespace`: it holds only type definitions,
and types declared in a namespace are visible to every other file in it without
qualification or an `open`.

`Log`, `Overlay` and `Daemon` are `internal` rather than `private`. A private module
is private to its *file*, which stops working the moment the files are split;
`internal` is assembly-wide and keeps the original intent. The accessibility goes
before the qualified name: `module internal MashedPotato.Log`.

<details>
<summary>The older single-file table, by module</summary>

| Module    | Role |
|-----------|------|
| `Interop` | delegate, struct, P/Invoke, and three helpers over them |
| `Target`  | a record describing one toggleable app |
| `Targets` | the Spotify and Chrome definitions |
| `Settings`| the shape of everything the JSON controls |
| `Config`  | reading that JSON, validating it, and holding the live settings |
| `App`     | find the app's real window, then launch / focus / minimize |
| `Snap`    | moving the current window to a third of its monitor |
| `Chord`   | the `WH_KEYBOARD_LL` hook and the two-key state machine |
| `Overlay` | the borderless "App Switcher" banner |
| `Daemon`  | tray icon, hidden message-pump window, `Application.Run` |

</details>

## Adding another app

Add an entry to `apps` in the JSON and reload. Nothing in the code knows about
Spotify or Chrome specifically - the tray menu and its tooltip are generated from
what the file says.

```json
{ "key": "V", "name": "VS Code", "processName": "Code",
  "paths": ["%LOCALAPPDATA%\\Programs\\Microsoft VS Code\\Code.exe"],
  "shellFallback": "code" }
```

`ExePaths` are tried in order and the first that exists is launched.
`ShellFallback` is used when none exist: either a URI scheme (Spotify's Store
build registers `spotify:`) or a bare name resolved by the shell.

The three targets each need a different launch route, which is why the record has
both fields:

| Target | How it starts |
|---|---|
| Spotify | `%APPDATA%\Spotify\Spotify.exe`, else the `spotify:` scheme the Store build registers |
| Chrome | `%ProgramFiles%` etc., else bare `chrome.exe` via its `App Paths` registry key |
| Windows Terminal | the `wt.exe` app execution alias, else bare `wt.exe` via `PATH` |

Windows Terminal is the awkward one. It is a packaged app, so the real binary
lives in `C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_...\` where
the ACLs stop you launching it directly, and neither `wt.exe` nor
`WindowsTerminal.exe` registers an `App Paths` key the way Chrome does. What does
work is the app execution alias at
`%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe` - a zero-byte reparse point that
`File.Exists` reports as present and `ShellExecute` follows to the package. The
fallback is a bare `wt.exe`, which resolves because that same WindowsApps folder
is on `PATH`. (`shell:AppsFolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App`
would be a third route if the alias is ever turned off in
Settings > Apps > Advanced app settings > App execution aliases.)

### When an app launches instead of switching

Almost always `processName`. It is matched against the running process image name,
so a display name (`Visual Studio Code`) matches nothing, the app looks not-running,
and every press launches another copy. Task Manager's *Details* tab shows the real
name; `Get-Process | Sort ProcessName` is the quicker way to find it.

`mashedpotato.log` now says which of the two reasons applied:

```
[activate] VS Code: launching - no running process is named 'Visual Studio Code'.
           If it IS running, check processName: it must be the image name from
           Task Manager's Details tab, without .exe
[activate] Spotify: launching - 7 process(es) named 'Spotify' are running but none
           has a visible window, so it is probably closed to the tray.
```

The second is normal and expected; the first means the name is wrong.

### Keys that are punctuation

Write the character. `"["`, `"]"`, `";"`, `"'"` all work, and are resolved through
the current keyboard layout with `VkKeyScan` - so they mean the key in that
position on *your* keyboard, not a fixed code. The `Keys` enum calls these
`OemOpenBrackets`, `Oem1` and so on; those names still work, but nobody should
have to know them. The tray menu maps back the other way, with `MapVirtualKey`,
so it reads `Ctrl+Shift+Alt+[`.

One consequence of going through the layout: a character and its shifted twin are
the same physical key, so `"["` and `"{"` bind identically.

## Picking the window

All three keep top-level windows that are not real windows. Spotify runs 7
processes with 12 top-level windows, one of which is titled
`GDI+ Window (Spotify.exe)`; Chrome runs 21+; Windows Terminal is a single
process whose real window is a `CASCADIA_HOSTING_WINDOW_CLASS`, alongside an
`OleDdeWndClass` and a `Windows Terminal <hash>` window that are both unowned
*and* titled and are only excluded by the visibility test.
`Process.MainWindowHandle` is not trustworthy here, so `App.windowsOf` keeps only
windows that are visible, unowned, and have a non-empty title - all three
conditions earn their place.

There is a fourth condition, for one window in particular. The desktop belongs to
`explorer.exe` and is visible, unowned and titled ("Program Manager", class
`Progman`), so it passes every test a real window does. Binding Explorer without
excluding it means the chord switches to the desktop and, because a window *was*
found, never launches File Explorer at all. `GetShellWindow()` names that window
directly, which beats matching on a class name.

Chrome differs from Spotify in that several of its windows are *real*, so
`App.pick` takes the topmost non-minimized one, falling back to the topmost
window when they are all minimized. `Interop.topLevelWindows` gets Z-order from
`GetTopWindow` + `GW_HWNDNEXT`, which is the documented walk - `EnumWindows`
merely happens to come back in that order. Z-order is what makes "the Chrome
window you were last using" win.

### Focusing without flickering the taskbar

Foreground rights are borrowed **before** the first `SetForegroundWindow`, not after
it fails. The order matters for a reason that is invisible in code review:

A refused `SetForegroundWindow` does not fail quietly. Windows flashes the target's
taskbar button instead - and an auto-hide taskbar slides out to show that flash
before hiding again. Since the `AttachThreadInput` fallback then succeeded, the
window ended up focused either way, so the old try-then-fall-back order looked
correct and merely made the taskbar flicker on every switch. On both monitors: each
display has its own taskbar window, `Shell_TrayWnd` and `Shell_SecondaryTrayWnd`.

Measured by polling each taskbar window's rectangle while driving a real chord. An
auto-hidden taskbar sits at `top = 1078` on a 1080-tall screen, with two pixels
showing, and slides to `1032` when revealed:

| | `SetForegroundWindow` | taskbars |
|---|---|---|
| try, then fall back | returned `False` | `1078 -> 1032` **revealed** |
| borrow, then call | returns `True` | `1078 -> 1078` stay hidden |

## Snapping a window

Snaps are plain hotkeys, checked before the chord state machine. Their modifiers
cannot collide with the prefix, which insists on Ctrl *alone*. If the banner
happens to be up, a snap takes it down on the way past.

Zones are data, in the JSON's `zones`. Adding one is a line in that file; nothing
in the code knows about a particular zone, and the tray menu is generated from the
list.

Geometry is two independent axes: a `Fraction` gives the size, an `Anchor`
(`Start`, `Middle`, `End`) gives the position, and `side` is just a friendly
spelling of the two anchors. Fractions stay as integer pairs rather than floats so
complementary zones still meet exactly - 2/3 and 1/3 of 1920 are 1280 and 640,
with nothing left over - and `End` anchors at `total - size` rather than at an
accumulated offset, so a right third starts precisely where a left two-thirds
finishes.

Checked on a 1920x1080 work area:

| side | width | height | rectangle |
|---|---|---|---|
| `left` | 2/3 | 1 | `0,0 1280x1080` |
| `right` | 1/3 | - | `1280,0 640x1080` |
| `top` | 1 | 1/2 | `0,0 1920x540` |
| `bottom` | - | 1/3 | `0,720 1920x360` |
| `topright` | 1/2 | 1/2 | `960,0 960x540` |
| `bottom-left` | 1/2 | 1/2 | `0,540 960x540` |
| `center` | 1/2 | 1/2 | `480,270 960x540` |
| `left` | 1/4 | 1/2 | `0,270 480x540` | The width is integer division of the work
area, so complementary fractions (2/3 and 1/3) meet exactly, with no gap or
overlap: 1280 + 640 = 1920.

Two details do all the work:

* **A maximized window ignores `SetWindowPos` geometry.** It has to be taken out
  of that state with `SW_RESTORE` first, or the keystroke silently does nothing.
  `IsZoomed` is the test.

* **A window's rect is bigger than the window looks.** DWM keeps an invisible
  resize border outside the visible frame - measured on this machine at 7px on
  the left, right and bottom of a normal window, 8px all round on a maximized
  one. Position by the window rect and every snapped window sits inset by that
  much. `Snap.allowingForBorder` takes the difference between `GetWindowRect` and
  `DWMWA_EXTENDED_FRAME_BOUNDS` and grows the target by it, so the *visible*
  edges land where they were asked to.

  Worked through with a real measurement from this machine - a terminal window
  whose rect was `(1256,0)-(1927,1087)` against a frame of `(1263,0)-(1920,1080)`,
  so borders of left 7, top 0, right 7, bottom 7:

  | | x | y | w | h |
  |---|---|---|---|---|
  | asked for (right third of 1920x1080) | 1280 | 0 | 640 | 1080 |
  | grown by the border, given to `SetWindowPos` | 1273 | 0 | 654 | 1087 |
  | visible frame that results | 1280 | 0 | 640 | 1080 |

  Measured end to end against a live window: the left two-thirds zone comes out as
  a raw rect of `-7,0 1294x1087` - deliberately hanging off the screen edge - for
  a visible frame of exactly `0,0 1280x1080`.

To snap to a different fraction or side, change the two lines that build `wanted`
in `Snap.rightThird`; everything else is generic.

### Moving between monitors

`Ctrl+Shift+Alt+Z` sends the current window to the next monitor and wraps from the
last back to the first. Monitors are ordered by device name (`\\.\DISPLAY1`,
`\\.\DISPLAY2`, ...), which is the numbering Display Settings shows -
`Screen.AllScreens` itself comes back in whatever order `EnumDisplayMonitors`
produced, which is not a promise.

The window keeps its size and its offset within the work area, so it lands in the
same place on the new screen. Two adjustments when the monitors differ:

* it is shrunk if it would not fit on the destination at all, and
* it is clamped so a smaller destination cannot leave it hanging off an edge.

A maximized window is restored first, moved, then maximized again - on its new
monitor, which is the point. `SetWindowPos` on a maximized window does nothing.

`Snap.movedBetween` is deliberately separate from the Win32 calls around it, so
the arithmetic can be checked on its own. Verified against a 1920x1080 pair and a
smaller synthetic monitor:

| window | move | result |
|---|---|---|
| 800x600 at 100,50 | mon1 → mon2 | `2020,50 800x600` |
| 800x600 at 2020,50 | mon2 → mon1 (wrap) | `100,50 800x600` |
| full screen | mon1 → mon2 | `1920,0 1920x1080` |
| 400x150 at 1500,900 | mon1 → 1280x720 | `2800,570 400x150` (clamped on) |
| 1920x1080 | mon1 → 1280x720 | `1920,0 1280x720` (shrunk) |

## Cycling the display

`Ctrl+Shift+Alt+1` switches the desktop to internal-only, waits two seconds, and puts
it back to extend.

It exists for one fault. Through a WD22TB4 Thunderbolt dock the external monitor
intermittently comes up dark: Windows enumerates it, applies a mode, marks it active
and composes a full desktop onto it - verified by sampling the framebuffer, which held
1537 distinct colours while the panel showed nothing - and no image ever reaches the
glass. Nothing is wrong that Windows can see, so nothing retries. Forcing a topology
change makes the driver re-modeset, and the picture appears.

`SetDisplayConfig` with null path and mode arrays, `SDC_APPLY`, and one
`SDC_TOPOLOGY_*` flag applies the arrangement Windows remembers for that topology -
the same route `DisplaySwitch.exe /internal` and `/extend` take, without paying for a
process launch.

Two details do the work:

* **The pause is load-bearing.** The point is to make the driver tear the link down
  and train it again; back-to-back calls let it coalesce the pair into no change at
  all. Two seconds is what was measured to work by hand on the dock this is for.

* **It runs off the UI thread.** The handler arrives on the message loop, which is
  also the thread the keyboard hook is installed on. Sleeping two seconds there would
  freeze the tray and the banner, and Windows silently drops a low-level hook that
  does not return promptly. `Display.cycle` starts the work on the thread pool and
  returns at once, behind a latch so a second press cannot interleave its own pair of
  calls and strand the desktop on internal-only - the one state you cannot see well
  enough to fix.

## The banner

Sized from the working area of whichever screen holds the foreground window:
three quarters of its width, centred, one fifth of its height, sitting on the
bottom edge. `WorkingArea` rather than `Bounds`, so it lands above the taskbar
instead of underneath it. Cyan, black caption, 20px orange border - it is
supposed to be hard to miss.

The border is `Padding` on the form rather than any custom painting: the form's
`BackColor` is the orange, and the caption is docked over everything inside the
padding. `Overlay.inner` subtracts it before fitting the font, so the text sizes
to the cyan area rather than the whole window.

The corners are rounded by `SetWindowRgn`, not by painting. A window region is the
shape Windows lets the window occupy at all - everything outside it is neither drawn
nor clickable. It is remade only when the banner's *size* changes, for the same reason
the font is: it is a GDI object, and the banner moves between monitors far more often
than it changes size.

Both edges of the border are rounded, which takes two regions rather than one. A
region applies to a child control exactly as it does to a top-level window, so the
caption gets its own: clipping its corners lets the form behind it - the orange - show
through them. The inner radius is the outer minus the border width, which is what
makes the two arcs concentric and keeps the ring a uniform 20px the whole way round.
Any larger and it pinches thin at the corners, any smaller and it bulges:

| | radius | size |
|---|---|---|
| banner | 26 | 1440x216 |
| caption | 6 | 1400x176 |

They are applied together and latched together - a rounded outside around a square
inside reads as a bug rather than as a style, so a refusal of either is retried on the
next show.

Two things to know about it:

* **The system takes ownership on success.** After `SetWindowRgn` returns non-zero the
  region handle belongs to Windows - it must not be deleted or set again, and Windows
  frees whatever region was there before. On *failure* it is still ours, and a missing
  `DeleteObject` on that path leaks a GDI object every time the banner is shown.

* **There is no antialiasing.** A region clips whole pixels, so the arcs are stepped
  rather than smooth. At a 26px radius against a 20px orange border it reads as
  rounded; up close the steps are visible. Smooth corners would need a layered window
  drawn with per-pixel alpha (`UpdateLayeredWindow`), which is a different design for
  this module - the border would stop being `Padding` and become something painted.

Two things about it matter more than they look:

* **`WS_EX_NOACTIVATE`, plus a `ShowWithoutActivation` override.** If the banner
  ever took the foreground, `App.activate`'s foreground test would see Mashed Potato
  rather than the app being toggled, and the minimize branch would never fire -
  the banner would silently break the feature it exists to advertise. Setting
  `Visible` is enough to honour that: `Form.SetVisibleCore` checks
  `ShowWithoutActivation` and uses `SW_SHOWNOACTIVATE`.

* **The font is measured in pixels, not points** (`GraphicsUnit.Pixel`), starting
  at 60% of the banner height and shrinking by 8% until it fits. Points would
  scale a second time on a high-DPI display and overflow the box. The banner is
  much wider than the text is at any size, so height is the binding constraint,
  and the fit leaves about 18% of it as padding.

The window is built once at startup, not on first use: creating a window inside
the hook callback would be slow, and a low-level hook has to return promptly or
Windows stops calling it.

## The font is never disposed - this one bites

`Overlay.applyFont` refits only when the banner's *size* changes, and no font handed
to the caption is ever disposed. That looks wasteful and is not:

`Control.Font` compares by value. Two separately-created fonts of the same family,
size, style and unit are `Equals`, and the setter **ignores an assignment it considers
unchanged, keeping the old object**. So a same-size refit leaves the label still
holding the font you were about to release. Dispose it and the next paint is a GDI+
`ArgumentException: Parameter is not valid` inside `Label.OnPaint`, which WinForms
renders as the white box with a red X (`Control.PaintWithErrorHandling`) and which
then takes the process down.

Two monitors of equal size are all it takes to reach it: the banner moves between
them, so its origin changes while its size does not - the refit runs, produces an
equal font, and the assignment quietly does nothing. On a single monitor it never
happens, which is what makes it look intermittent.

## Notes on the design

* The three-way decision is a single `match`, and the case order is load-bearing:

  ```fsharp
  match pick windows with
  | None -> launch target
  | Some hwnd when IsIconic hwnd -> focus hwnd
  | Some hwnd when pids.Contains(processIdOf (GetForegroundWindow())) ->
      ShowWindow(hwnd, SW_MINIMIZE) |> ignore
  | Some hwnd -> focus hwnd
  ```

  Minimized must be matched before foreground: `SW_MINIMIZE` does not hand the
  foreground to another window, so `GetForegroundWindow()` goes on naming the
  window that was just minimized. Reverse the two and the chord stops toggling
  back - it re-minimizes an already-minimized window and looks dead.

* `Chord.callback` and `Chord.hook` are module-level mutables on purpose: the
  delegate must outlive `SetWindowsHookEx` or the GC collects it while Windows
  still holds the pointer.

* Windows calls a `WH_KEYBOARD_LL` hook on the thread that installed it, so the
  callback already runs on the UI thread. The hidden `Form` is not there for
  thread affinity; it is there to defer the work out of the callback, which has
  to return promptly. Raising and lowering the banner is cheap enough to do
  inline, which is why it is instant.

* `Chord.install` returns `Result<unit, int>` carrying the Win32 error code.

* `OpenProcess`'s second parameter is `inheritHandle`, not `inherit` - reserved word.

* Nothing may escape the hook callback. Windows calls it across a native boundary,
  where an exception does not reach the message loop's handler - it terminates the
  process, and from outside that looks like the daemon simply vanishing.

* The `Dto` module holding the JSON shapes is deliberately **not** private.
  System.Text.Json only sees public types; with a private one, serialization
  silently produces `{}` rather than failing - which is exactly what the first
  starter file came out as.

* `mashedpotato.log`, under `%LOCALAPPDATA%\Mashed Potato\`, catches `AppDomain.UnhandledException`,
  `Application.ThreadException`, and the hook, banner and app handlers. A daemon with
  no console and no window of its own fails invisibly; this is the only reason the
  font bug above was diagnosable rather than guessed at.

## The icon

The artwork - `icon/artwork.jpg` - is a cartoon on a sheet of graph paper: landscape,
outlined, spattered with chunks, and with *SMASH!* lettered on the hammer. Nothing
about that survives a 16px tray icon on its own terms, so `icon/build-icon.py` turns
it into `mashed.ico` in three steps.

**Keying out the paper.** Not a threshold on brightness, which would also hollow out
the white puffs of smoke inside the drawing, but a flood fill inwards from the
border over pixels that are both pale and unsaturated. Only paper the border can
reach is removed; anything the ink encloses stays. The fill is then grown two pixels
into the drawing, because the ring just inside an outline is JPEG-pale and reads as
a white halo the moment the icon sits on a dark taskbar.

**Two crops, not one.** The source is 1408x768 and an icon is square, so something
has to go either way:

| | Crop | Sizes | Keeps |
|---|---|---|---|
| wide | 660px at (330, 90) | 40 and up | the whole scene - potato, hammer, handle, flying chunks, the lettering |
| tight | 560px at (420, 130) | 32 and below | the potato and the hammer head, and nothing else |

The wide crop is legible down to about 48px and turns to noise below it: at 24px the
chunks and the handle are single scattered pixels that read as dirt around the
subject rather than as anything. The tight crop throws all of that away to buy about
20% more size for the two shapes that still carry meaning at 16px - a gold potato
and a dark hammer head driven into it.

**Downsampling.** A box filter over *premultiplied* alpha. Averaging straight RGBA
lets the colour of transparent pixels - black, here - bleed into every edge, which
at these sizes is most of the icon; premultiplying weights each pixel's colour by
its own alpha, so a pixel that is not there contributes nothing but transparency.

The file carries nine frames: 16, 20, 24, 32, 40, 48, 64, 128, 256. The first three
are the tray at 100%, 125% and 150% scaling, which is the whole reason for the
unusual 20 and 24. Frames up to 64 are stored as DIBs and the two largest as PNG,
which is what every icon since Vista does - a 256x256 BGRA bitmap is a megabyte
uncompressed, and the format has allowed a PNG in that slot since precisely the
release that introduced the size.

`System.Drawing` will not hand back the 256 frame - ask it for 256 and it returns
the 128 - but the shell reads it, and nothing in this program asks for either.

To rebuild after editing the artwork:

    python3 icon/build-icon.py

Pure standard library, and roughly a second. The one thing it shells out for is
decoding the JPEG, which `powershell.exe` and `System.Drawing` do: Python has no
JPEG decoder in the standard library, and the alternative is a dependency for one
call. `build.sh` copies `mashed.ico` to the staging directory alongside the sources;
the `.fsproj` both sets it as `ApplicationIcon` and embeds it, which are two
different things - the first is what Explorer and the taskbar read off the file, the
second is what `Daemon.trayIcon` reads at run time. A tray icon loaded from a loose
file beside the executable is one missing file away from a blank square.

## Layouts

A zone moves the window in front of you. A layout moves *all* of them, to wherever
they belong on whatever the machine is plugged into right now. `Ctrl+Shift+Alt+0`.

    "placement": { "applyLayout": "0", ... },
    "layouts": [
      {
        "name": "Office (WD25, two Dells)",
        "when": { "dock": "WD25", "monitors": 2 },
        "windows": [
          { "app": "VS Code",          "monitor": 1, "rect": "0, 0, 2/3, 1" },
          { "app": "Windows Terminal", "monitor": 1, "rect": "2/3, 0, 1/3, 1" },
          { "app": "Chrome",           "monitor": 2, "rect": "0, 0, 1, 1" }
        ]
      }
    ]

The key lives in `placement` beside `nextMonitor` and `cycleDisplay`, because it is
a placement action and it answers to the same held prefix. The arrangements live at
the top level because there is a list of them and they are the part that gets
edited. Each half is checked against the other: a key with no layouts, or layouts
with no key, is a configuration error rather than something that silently does
nothing.

### Why profiles, and why the dock

The same three windows want a different arrangement at each desk, and the machine
cannot be asked which desk it is at. What it can be asked is which monitors are
attached and which devices are present, and between them those identify a desk.

Monitor *count* alone does not, which is the trap. A laptop docked to two externals
with the lid shut has two monitors; the same laptop at home, lid open, with one
external also has two. Only the dock separates those two cases, so `when` can ask
about either or both:

| | Matches when |
|---|---|
| `"dock": "WD25"` | some device present is named like that - `WD25` finds `Dell Pro Dock WD25` |
| `"monitors": 2` | exactly that many monitors are attached |
| both | both hold |
| neither (`when` omitted) | always - which is how the last profile becomes the fallback |

On top of whatever `when` says, **a profile only matches if every monitor it names
is actually attached**. Nobody writes that condition; it is the difference between a
profile being *chosen* and being *carryable*, and a profile that cannot be carried
out should never win. It is also what makes `"monitors"` unnecessary nearly always -
see below.

### Counting monitors is a trap

`"monitors": 2` was how the shipped profiles first told the office desk from the
home one, and it is subtly wrong. Two externals with the lid **shut** is two
monitors. Open the lid and it is three, the profile stops matching the desk it was
written for, and the fallback takes over - so plugging the dock back in lands
everything on the laptop screen and looks exactly like the dock not being detected.

    1  \\.\DISPLAY1  Dell P2222H (DP)     0,0 1920x1080  PRIMARY
    2  \\.\DISPLAY2  Dell P2222H (DP)     1920,0 1920x1080
    3  \\.\DISPLAY3  Generic PnP Monitor  3840,802 1920x1200   <- the lid, open

What the office layout actually needs is not "two monitors" but "monitors 1 and 2
exist", which is true with the lid either way - and which is checked automatically,
from the profile's own `windows` list. So the shipped profiles ask about the dock
and nothing else.

`"monitors"` is still there, and still means *exactly* that many. It is the right
tool when two desks differ **only** by how many screens they have. It is the wrong
tool for anything a lid can change.

**Profiles are tried in order and the first match wins**, so they run specific to
general and the catch-all goes last. There is deliberately no `"dock": "none"`: a
laptop advertises an ACPI `Docking Station` node whether or not anything is plugged
into it, so "no dock" is not a thing that can be honestly tested for. Ordering says
the same thing without lying about it.

Finding a dock means walking the whole device tree - 300 devices and about 130ms on
this machine - so it happens once when the key is pressed, never in the keyboard
hook, and not at all if no profile mentions a dock.

One wrinkle worth knowing: the name to match is the device's **friendly name**, not
its description. The WD25 describes itself as `WinUsb Device` and is only called
`Dell Pro Dock WD25` by the friendly name, so `presentDeviceNames` reads that first
and falls back to the description - the order Device Manager resolves a name in.
Reading only the description, which is the obvious first guess, finds no dock at all.

### Naming a monitor

`"monitor"` takes a number from 1, `"primary"`, or any part of a monitor's name:

    "monitor": 2                  the second monitor
    "monitor": "primary"          wherever the taskbar's clock is
    "monitor": "Dell P2222H"      matched case-insensitively, anywhere in the name

Numbering is by adapter name - `\\.\DISPLAY1`, `\\.\DISPLAY2` - which is the
numbering Display Settings shows and the same order `nextMonitor` cycles in. Only
*attached* monitors are counted, so with the lid shut the two externals are 1 and 2,
and opening the lid makes the built-in panel 1 and pushes them to 2 and 3.

That is the one thing about a layout that can quietly go wrong, so the tray menu
lists what is attached right now, by number and by name, along with any docks it can
see and which profile they add up to:

    Ctrl+Shift+Alt+0   →   Layout: Office (WD25, two Dells)
            1: Dell P2222H (DP) 1920x1080 (primary)
            2: Dell P2222H (DP) 1920x1080
            Dell Pro Dock WD25

Those are the strings to paste into a new profile. The menu works it out the same
way the keystroke does, so it cannot advertise one profile and apply another.

### Saying only which monitor

Some windows are worth arranging exactly and some are only in the wrong place. A
slot with no `rect` is the second kind:

    { "app": "Microsoft Word", "monitor": 1, "instances": "all" }

That window keeps its size and its offset within the work area, and simply moves to
monitor 1 - the same arithmetic `Ctrl+Shift+Alt+Z` uses, clamped so a smaller
destination cannot push it off the edge. A window already on that monitor is left
*completely* alone rather than placed where it already is, which matters more than
it sounds: placing goes through the path that restores a maximized window first, so
"moving" a maximized window nowhere would quietly un-maximize it.

`"instances": "all"` takes every window the app has open rather than the front one.
Word has a window per document and they come and go, so there is no way to name them
in a file and no point trying; `all` says what is actually meant. The default stays
`front`, because for Chrome - which is regularly six windows - moving all of them is
almost never what anyone wants.

The two settings are independent. `all` with a rect is legal and means what it says:
every window of that app, stacked on the same rectangle.

### Minimized windows, and the one that is not obvious

A minimized window's rect is `-32000, -32000`. That is Windows parking it off-screen
and says nothing about where it belongs, so it cannot simply be moved.

What a slot does about it depends on whether it gave a rect, and the split follows
from what each one is asking for:

* **With a rect** the window is restored first. "Exactly here" cannot be true of a
  minimized window, and one that silently accepts the geometry and stays minimized
  looks precisely like the key having done nothing.

* **Without a rect** only the monitor was asked about, so the window keeps its state
  and its *restore position* is moved instead - `GetWindowPlacement`,
  `rcNormalPosition`, `SetWindowPlacement`, with `showCmd` left alone so it stays
  minimized. Nothing appears on screen; the window has simply come to belong to the
  other monitor, and that is where it comes back when it is un-minimized.

This is what makes `all` mean anything for Word. Of the four Word windows open on
this machine while writing it, **three were minimized** - so restoring them all to
move them would have thrown four documents up on screen, and skipping them would
have left the instruction applying to one window out of four.

`rcNormalPosition` is in *workspace* coordinates: screen coordinates offset by the
origin of the primary monitor's work area. With the taskbar at the bottom, or
auto-hidden as it is here, that origin is `(0, 0)` and the two are identical - which
is exactly why the correction is easy to leave out and never notice it missing.

### Following the hardware

    "applyLayoutOnChange": 4

With that set, docking or undocking rearranges the windows on its own, four seconds
after the hardware stops changing. `"off"` or leaving it out keeps the keystroke as
the only way in.

Windows already broadcasts what is needed, to every top-level window, with nothing
to register for:

| | Sent when |
|---|---|
| `WM_DISPLAYCHANGE` | the desktop's shape changes - a monitor arriving or leaving, a resolution or topology change |
| `WM_DEVICECHANGE` with `DBT_DEVNODES_CHANGED` | the device tree changes at all |

Both are listened to rather than just the first, because they answer different
halves of the question: a dock can arrive before its monitors come up, and a monitor
can arrive with no dock involved at all.

**The hidden window has to be a real top-level window for any of this to arrive.** A
message-only window - `HWND_MESSAGE`, the obvious choice for something invisible
that exists only to receive messages - is cheaper and would never get one of these,
because broadcasts go to top-level windows and a message-only window is not one.
Invisible is fine; not top-level is not. The window here was already a plain hidden
`Form`, so this cost nothing, but it is the kind of optimisation that would have
silently broken the feature.

Three things then stand between a broadcast and moving someone's windows.

**Settling.** A dock arriving is dozens of device notifications and several display
changes over some seconds, and a layout applied to a desktop that is still
rearranging itself achieves nothing. The timer is **restarted** on every broadcast
rather than started, so a storm collapses into one pass after the last of them. Four
seconds is a starting point rather than a measurement - it is the one number here
that depends on how long a particular dock takes to bring its monitors up, which is
why it is in the file and not in the code.

**Asking whether anything actually changed.** `DBT_DEVNODES_CHANGED` fires for every
USB device on the machine. Rearranging someone's windows because they plugged in a
phone would be indefensible, so the event path recomputes which profile fits and
does nothing unless the answer differs from what was already in force. The
broadcasts cannot say *what* changed; comparing the conclusion is what turns a
stream of meaningless notifications into "the desk changed".

That is also why the keystroke and the event path are deliberately different.
`Ctrl+Shift+Alt+0` is **unconditional** - it is what you press when something has
been dragged out of place and you want it back, and "nothing has changed" is not an
answer to that. The event path is conditional, because nobody asked it to do
anything at all.

**Starting in agreement.** The profile in force is seeded from the configuration
when it is read, without moving anything. Otherwise launching the daemon, or
reloading the config, would tidy a desk nobody asked it to touch. It begins by
agreeing with what is already there and acts on the next change.

The work runs on the thread pool, not the message loop - and so does the keystroke
path, which is the one place this program hands a key's work to a thread rather than
to the loop. The decision walks the whole device tree, and placing windows can block
on an unresponsive application; the message loop is also the thread the keyboard
hook is delivered to, which Windows drops if it stops answering promptly.

The keystroke needs it for a second reason: **it auto-repeats.** A low-level hook
sees a held key as a stream of key-downs, indistinguishable from real ones, and four
passes in a single second turned up in the log the first time someone leant on
`Ctrl+Shift+Alt+0`. Queued on the message loop those serialise, each paying for its
own walk of the device tree. On the thread pool the latch inside `Layout` drops them
outright: one pass runs, the repeats find it busy and give up.

The tray menu says whether it is armed:

    Ctrl+Shift+Alt+0   →   Layout: Office (WD25, two Dells)
            1: Dell P2222H (DP) 1920x1080 (primary)
            2: Dell P2222H (DP) 1920x1080
            Dell Pro Dock WD25
            follows the hardware, 4s after it settles

### Rects, and why they are not zones

    "rect": "left, top, width, height"

Four fractions of the monitor's working area. A zone is a size plus an anchor, which
cannot say "from a third across to a half" - and a layout has to, because it has to
express arrangements that were never snapped to anything. Fractions stay integer
pairs for the reason [zones do](#snapping-a-window): 1/3 and 1/6 of 1920 are 640 and
320, and the windows meet exactly. `left` and `top` may be `0`; a width or height
may not.

A rect that runs off the monitor - `left` 2/3 with `width` 2/3 - is rejected when
the file is read, with the arithmetic done by cross-multiplication so the check is
exact rather than floating point.

### What it does to a window

Windows are found the way `Ctrl+K` finds them - real windows of a process of that
name, the topmost non-minimized one unless the slot said `all` - and moved by
`Snap.place` or `Snap.toMonitor`, the same functions zones and `nextMonitor` go
through, so the invisible-border correction happens in exactly one place.

Apps that are not running are **skipped, not launched**. Launching would mean
waiting for a window to exist before it could be placed, and a keystroke that
sometimes takes ten seconds is worse than one that says what it could not do. What
it could not do arrives as a tray balloon: *"Office: Spotify has no window open"*.

### The three profiles as shipped

Measured off the running desktop rather than guessed at. Lid shut, WD25, two Dell
P2222Hs side by side at 1920x1080, taskbar auto-hidden so the working area is the
whole screen:

| | Monitor | Visible frame | As a rect |
|---|---|---|---|
| VS Code | 1 | `0,0 1280x1080` | `0, 0, 2/3, 1` |
| Windows Terminal | 1 | `1280,0 640x1080` | `2/3, 0, 1/3, 1` |
| Chrome | 2 | `1920,0 1920x1080` | `0, 0, 1, 1` |

Those three windows span 3840 pixels: VS Code across the first third of it, the
terminal to the half, Chrome across the rest. **That** is the arrangement, and it is
what the other two profiles keep:

| | Office | Laptop alone | Home |
|---|---|---|---|
| VS Code | mon 1, `0, 0, 2/3, 1` | mon 1, `0, 0, 1/3, 1` | mon 2, `0, 0, 1/3, 1` |
| Windows Terminal | mon 1, `2/3, 0, 1/3, 1` | mon 1, `1/3, 0, 1/6, 1` | mon 2, `1/3, 0, 1/6, 1` |
| Chrome | mon 2, `0, 0, 1, 1` | mon 1, `1/2, 0, 1/2, 1` | mon 2, `1/2, 0, 1/2, 1` |
| Microsoft Word | mon 1, all, no rect | - | mon 2, all, no rect |
| Obsidian | mon 1, all, no rect | - | mon 2, all, no rect |

Word and Obsidian give no rect: they are only ever in the wrong *place*, and every
window of each goes, not just the front one. Neither appears under *Laptop alone*,
where there is one monitor and nothing to choose. Under *Home* they follow that
profile's rule - everything on the external, the laptop panel left empty.

The single-screen fractions are the two-monitor ones halved, which is what makes it
the same arrangement rather than a new one: the windows keep their proportions and
their left-to-right order, on a third of the pixels.

Home puts all three on monitor 2 and leaves the laptop panel empty, which is the
whole point of that profile. It assumes the built-in panel is `\\.\DISPLAY1` and so
numbered 1, which is the usual arrangement on a laptop - if the tray menu says
otherwise, replace the `2` with part of the external monitor's name. Shutting the
lid at home leaves one monitor, no profile matches two, and it falls through to
*Laptop alone* - which puts the same arrangement on the only screen there is. That
fallthrough is the ordering doing its job rather than a special case.

## Known limits

* `Ctrl+K` is swallowed globally unless the foreground process is listed in
  `Config.PassThroughIn`. The conflicts that actually apply here are address-bar
  search in Chrome and `Ctrl+K, Ctrl+C` for "comment selection" in VS Code.

  The terminal is *not* one of them, despite the obvious guess. `Ctrl+K` is
  kill-to-end-of-line in the **emacs** keymap; this shell runs `bindkey -v`,
  where `^K` is `self-insert` in `viins` and `undefined-key` in `vicmd` (bash's
  `vi-insert` does not bind `kill-line` either). So the chord costs nothing at
  the prompt, and `"WindowsTerminal"` should stay out of the pass-through list -
  adding it would stop the chord firing while the terminal is focused, leaving
  `Ctrl+K, T` able to summon the terminal but never to minimize it.

* `Daemon.trayText` truncates at 63 characters because `NotifyIcon.Text` throws
  above that. Three bindings comes to 60, so a fourth will start eating the text.
* Keystrokes inside an elevated window are invisible to a non-elevated hook.
  AutoHotkey has the same constraint.
* Synthetic keystrokes are ignored on purpose (`LLKHF_INJECTED`), so the chord
  will not fire from remapping software such as PowerToys Keyboard Manager.
* `ExtractAssociatedIcon` on `Mashed.exe` returns 32x32 whatever the caller asks
  for. It is not used here - the tray reads the embedded copy - but it is the
  obvious thing to reach for, and it quietly ignores the other eight frames.
* A held `Ctrl+Shift+Alt+0` still costs one pass per burst rather than none - the
  latch drops the repeats, it does not stop them arriving. A low-level hook cannot
  tell an auto-repeat from a real key-down, which is where that would have to be
  fixed.
* Four seconds of settling is a guess, not a measurement - no dock was plugged or
  unplugged while this was written. It is in the configuration for that reason. If
  windows land while the monitors are still coming up, raise it.
* Nothing watches for sleep or resume. Docking usually raises the broadcasts anyway
  on wake; a lid closed and opened on the same hardware raises none, and does not
  need to.
* A layout skips an app that is not running rather than launching it, and says so
  in a balloon. Placing a freshly launched window means waiting for it to appear,
  which is the kind of wait that turns a keystroke into a pause.
* The workspace-coordinate correction on `rcNormalPosition` is the one path here
  that this machine cannot exercise: its taskbar is auto-hidden, so the primary
  work area starts at `(0, 0)` and the correction is provably a no-op. It only
  starts to matter with a taskbar docked to the top or the left.
* `when` cannot say "the monitor that is not the laptop's". Windows can be asked -
  `QueryDisplayConfig` reports `OUTPUT_TECHNOLOGY_INTERNAL` - but not cheaply, and
  matching the panel by name gets to the same place in one line of configuration.
* Only one Mashed Potato runs at a time; the C# and F# builds share a mutex name, so
  whichever starts second exits silently.
