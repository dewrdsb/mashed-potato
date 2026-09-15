// 7 of 12 - the desktop topology
//
// One action: re-train the link to an external display by switching the desktop to
// internal-only and straight back to extend.
//
// This exists because of a specific fault. Through a WD22TB4 Thunderbolt dock, the
// DisplayPort tunnel intermittently brings a monitor up dark: Windows enumerates it,
// applies a mode, marks it active and composes a full desktop onto it, and no image
// ever reaches the panel. Nothing is wrong that Windows can see, so nothing retries.
// Forcing a topology change makes it re-modeset, and the picture appears.
//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

module MashedPotato.Display

open System
open System.Threading
open System.Threading.Tasks

open Interop

/// How long internal-only is left in force before extend goes back on.
///
/// The two calls cannot simply follow one another: the point of the exercise is to
/// make the driver tear the link down and train it again, and back-to-back calls
/// let it coalesce them into no change at all. Two seconds is what was measured to
/// work by hand on the dock this exists for.
let private settleMs = 2000

/// Set while a cycle is in flight. A second press during those two seconds would
/// interleave its own pair of calls with this one and could leave the desktop
/// sitting on internal-only, which is precisely the state the user cannot see.
let mutable private running = 0

/// Passing no path or mode arrays, just SDC_APPLY and one SDC_TOPOLOGY_* flag, asks
/// Windows to apply the arrangement it has remembered for that topology - the same
/// route DisplaySwitch.exe takes. Returns ERROR_SUCCESS (0) or a Win32 code.
let private apply topology name =
    match SetDisplayConfig(0u, 0n, 0u, 0n, SDC_APPLY ||| topology) with
    | 0 -> true
    | code ->
        Log.note "display" $"Switching to {name} failed with Win32 error {code}."
        false

/// Internal-only, then extend. Bound to a key in the placement section.
///
/// The work runs on a thread pool thread rather than the UI thread, which is where
/// the handler arrives: sleeping two seconds on the message loop would freeze the
/// tray icon and the banner for the duration, and the hook itself is on that thread.
let cycle () =
    // Take the latch only if it is clear. CompareExchange returns what was there.
    if Interlocked.CompareExchange(&running, 1, 0) = 0 then
        Task.Run(
            Action(fun () ->
                try
                    try
                        if apply SDC_TOPOLOGY_INTERNAL "internal only" then
                            Thread.Sleep settleMs
                            apply SDC_TOPOLOGY_EXTEND "extend" |> ignore
                    with error ->
                        Log.write "display" error
                finally
                    Interlocked.Exchange(&running, 0) |> ignore))
        |> ignore
