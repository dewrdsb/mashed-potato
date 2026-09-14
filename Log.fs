// 2 of 11 - the log file
//
// Appends to mashedpotato.log under %LOCALAPPDATA%. A daemon with no console and no
// window of its own fails invisibly: without this, anything that goes wrong here is
// unobservable, and all you see is that the process is no longer there.
//
// WHERE A WINDOWS APPLICATION PUTS ITS FILES
//
//   %APPDATA%        per-user, and *roams*: on a domain account the profile service
//   (Roaming)        copies it between machines at logon and logoff. Settings go
//                    here - small, precious, and wanted on every machine.
//
//   %LOCALAPPDATA%   per-user, stays on this machine. Logs, caches, downloaded
//   (Local)          state, anything large or machine-specific. Also
//                    %LOCALAPPDATA%\Programs\<App> for a per-user install, which is
//                    what VS Code and Teams do.
//
//   %PROGRAMDATA%    shared by every user on the machine.
//   %TEMP%           throwaway.
//
// So the config sits in %APPDATA% and this log does not. A log in a roaming profile
// is copied across the network at every sign-in and grows without bound - a classic
// way to make logons slow and irritate whoever runs the domain.
//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

module internal MashedPotato.Log

open System
open System.IO

// Path combining rather than string concatenation is the .NET habit: it gets the
// separators right and is what everyone will expect to read.
let private folder =
    Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData,
        "Mashed Potato")

let private file = Path.Combine(folder, "mashedpotato.log")

let private append (context: string) (text: string) =
    try
        // The folder may not exist yet; creating it is idempotent and cheap.
        Directory.CreateDirectory folder |> ignore
        let stamp = DateTime.Now.ToString("O")
        File.AppendAllText(file, $"{stamp} [{context}] {text}\n\n")
    with _ ->
        () // logging must never be the thing that takes the process down

let write (context: string) (error: exn) = append context (string error)

/// For things worth explaining that are not failures.
let note (context: string) (message: string) = append context message
