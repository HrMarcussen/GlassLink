using GlassLink.Capture;
using GlassLink.Capture.Windows;

namespace GlassLink.Sim;

/// <summary>One display of an X-Plane aircraft: the title of its pop-out window, the command that opens and closes
/// it, and its entry in the aircraft's array of popup states.</summary>
public sealed record XPlaneDisplay(string Title, string Command, int StateIndex);

/// <summary>
/// An X-Plane aircraft whose displays GlassLink can pop out: the aircraft draws them as pop-out windows of its own,
/// opened by its commands, so no camera has to move and nothing is clicked in the cockpit.
/// </summary>
public sealed record XPlaneProfile(string Key, Func<string, bool> Matches, IReadOnlyDictionary<string, XPlaneDisplay> Displays,
    string? ReinstateCommand, string? StateArray, int Frame, string? DimmingName)
{
    /// <summary>How the DMC finds a display's pop-out: X-Plane's window by its exact title, cropped by X-Plane's frame,
    /// kept out of Alt+Tab and the taskbar.</summary>
    public WindowRule? Rule(string display) => Displays.TryGetValue(display, out var d)
        ? new WindowRule(new WindowMatch(XPlaneClient.Process, XPlaneProfiles.WindowClass, null, d.Title, null, null), Frame, ToolWindow: true)
        : null;
}

public static class XPlaneProfiles
{
    /// <summary>X-Plane's window class, for its main window and every pop-out.</summary>
    public const string WindowClass = "X-System";

    /// <summary>
    /// The ToLiss Airbus family (A319, A320neo, A321, A339, A340). Measured 2 Oct 2026 with the A321 1.7.2 and the A339
    /// 1.1 on X-Plane 12.4 (the same window titles and commands in both): with the ISCS options "Use popout windows for popups" and "Save popup config on quit" its popups are
    /// ordinary windows; AirbusFBW/PopUp* toggles one, toliss_airbus/reinstatePopups brings back every one that was
    /// popped out at the end of the last flight. The windows are drawn sharp at any size, inside a 15 px X-Plane frame,
    /// and they dim with the cockpit's brightness knobs themselves.
    /// </summary>
    public static readonly XPlaneProfile ToLiss = new(
        "ToLiss",
        path => path.Contains("ToLiss", StringComparison.OrdinalIgnoreCase),
        new Dictionary<string, XPlaneDisplay>
        {
            ["pfd"] = new("ToLiss Captain Left DU", "AirbusFBW/PopUpPFD1", 2),
            ["fo_pfd"] = new("ToLiss Copilot Right DU", "AirbusFBW/PopUpPFD2", 3),
            ["nd"] = new("ToLiss Captain Right DU", "AirbusFBW/PopUpND1", 4),
            ["fo_nd"] = new("ToLiss Copilot Left DU", "AirbusFBW/PopUpND2", 5),
            ["ecam_upper"] = new("ToLiss Upper ECAM", "AirbusFBW/PopUpEWD", 6),
            ["ecam_lower"] = new("ToLiss Lower ECAM", "AirbusFBW/PopUpSD", 7),
        },
        ReinstateCommand: "toliss_airbus/reinstatePopups",
        StateArray: "AirbusFBW/PopUpStateArray",
        Frame: 15,
        DimmingName: "the ToLiss dims its pop-outs itself");

    public static IReadOnlyList<XPlaneProfile> BuiltIn { get; } = [ToLiss];

    /// <summary>The profile for an aircraft (its .acf path relative to the X-Plane folder); null if there is none.</summary>
    public static XPlaneProfile? Select(string aircraftPath) =>
        aircraftPath.Length == 0 ? null : BuiltIn.FirstOrDefault(p => p.Matches(aircraftPath));
}
