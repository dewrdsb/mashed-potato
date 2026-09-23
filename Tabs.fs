// 5 of 13 - the tabs inside a browser window
//
// One question and one action: which tab is this, and switch to that one. The only
// file that reaches inside another application rather than pushing its windows
// around from the outside.
//
// Compile order is load-bearing in F#: a file may only use what is declared
// in files listed before it in MashedPotato.fsproj, so that list is the dependency
// graph, checked by the compiler.

module MashedPotato.Tabs

open System
open System.Windows.Automation

// WHAT UI AUTOMATION IS, AND WHY IT IS THE ANSWER HERE
//
// UI Automation is the accessibility API - the one screen readers use. An
// application publishes a tree of elements with roles (Window, Tab, TabItem,
// Button), names, and *patterns*: small interfaces describing what can be done to
// an element. A browser tab has the SelectionItem pattern, and SelectionItem has a
// Select method, which is precisely "switch to this tab".
//
// Every other route to the same place is worse:
//
// * The remote debugging port - CDP or WebDriver BiDi - can do it, but the browser
//   has to be started with --remote-debugging-port, which means a restart, and it
//   leaves a port open that any local process can drive the browser through. A high
//   price for switching tabs.
// * An extension with native messaging is the sanctioned route and needs an
//   extension built, signed and installed.
// * Sending Ctrl+1..8 picks a tab by position, which changes every time a tab is
//   opened or closed.
//
// Accessibility costs something: a browser builds its accessibility tree when a
// client first asks for it, and keeps it up to date afterwards. It is the same
// work done for any screen reader, and it is why nothing here asks unless a
// binding actually names a tab.
//
// These calls are cross-process COM and take a couple of hundred milliseconds, so
// none of them belong on the message loop - App runs them on the thread pool,
// which is also where Microsoft asks UI Automation clients to call from.

/// One tab in a window: what it says, whether it is the one showing, and how to
/// make it the one showing.
type Tab =
    { Name : string
      IsSelected : bool
      Select : unit -> unit }

let private ofType (control: ControlType) =
    PropertyCondition(AutomationElement.ControlTypeProperty, control)

/// Every tab in a window, or an empty list if this window has no tab strip - which
/// is the answer for most windows, and for a browser that has not finished starting.
///
/// The strip is found by a subtree search, which sounds alarming inside a browser
/// and is not: the search is depth-first from the window, the chrome sits above the
/// page in that tree, and the strip is reached long before any page content. Asking
/// the strip for its *children* then keeps the tab list from descending anywhere at
/// all.
let private tabsIn (hwnd: nativeint) =
    let root = AutomationElement.FromHandle hwnd

    if isNull root then
        []
    else
        match root.FindFirst(TreeScope.Subtree, ofType ControlType.Tab) with
        | null -> []
        | strip ->
            let items = strip.FindAll(TreeScope.Children, ofType ControlType.TabItem)

            [ for index in 0 .. items.Count - 1 ->
                let item = items.[index]

                let selected =
                    match item.GetCurrentPattern(SelectionItemPattern.Pattern) with
                    | :? SelectionItemPattern as pattern -> pattern.Current.IsSelected
                    | _ -> false

                { Name = item.Current.Name
                  IsSelected = selected
                  Select =
                    fun () ->
                        match item.GetCurrentPattern(SelectionItemPattern.Pattern) with
                        | :? SelectionItemPattern as pattern -> pattern.Select()
                        | _ -> () } ]

/// The first tab in this window whose title contains `wanted`, matched
/// case-insensitively. Titles are what the tab strip shows and all it exposes - the
/// URL is not in the accessibility tree - so this matches what you can read.
///
/// Every call here can throw, and for an ordinary reason: a window that closes
/// while its tree is being walked takes the elements with it. That is not worth a
/// balloon, so it comes back as "no such tab".
let find (wanted: string) (hwnd: nativeint) =
    try
        tabsIn hwnd
        |> List.tryFind (fun tab -> tab.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
    with
    | :? ElementNotAvailableException -> None
    | error ->
        Log.write "tabs" error
        None
