using System.Text.Json;
using System.Text.Json.Nodes;
using GlassLink.Capture;
using GlassLink.Capture.Windows;
using GlassLink.Core.Config;

namespace GlassLink.Sim;

/// <summary>One display of an X-Plane aircraft: the title of its pop-out window, the command that opens and closes
/// it, and its entry in the aircraft's array of popup states.</summary>
public sealed record XPlaneDisplay(string Title, string Command, int StateIndex);

/// <summary>
/// An X-Plane aircraft whose displays GlassLink can pop out: the aircraft draws them as pop-out windows of its own,
/// opened by its commands, so no camera has to move and nothing is clicked in the cockpit.
/// </summary>
public sealed record XPlaneProfile(string Key, IReadOnlyDictionary<string, XPlaneDisplay> Displays,
    string? ReinstateCommand, string? StateArray, int Frame, string? DimmingName)
{
    /// <summary>The profile applies to an aircraft whose .acf path (relative to the X-Plane folder) contains its key.</summary>
    public bool Matches(string aircraftPath) => aircraftPath.Contains(Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>How the DMC finds a display's pop-out: X-Plane's window by its exact title, cropped by X-Plane's frame,
    /// kept out of Alt+Tab and the taskbar.</summary>
    public WindowRule? Rule(string display) => Displays.TryGetValue(display, out var d)
        ? new WindowRule(new WindowMatch(XPlaneClient.Process, XPlaneProfiles.WindowClass, null, d.Title, null, null), Frame, ToolWindow: true)
        : null;
}

/// <summary>The X-Plane profiles: the "sim": "xplane" entries of <see cref="Profiles"/>, built-in and from config.json.</summary>
public static class XPlaneProfiles
{
    /// <summary>X-Plane's window class, for its main window and every pop-out.</summary>
    public const string WindowClass = "X-System";

    /// <summary>The built-in ToLiss profile (Profiles' remarks say what was measured).</summary>
    public static XPlaneProfile ToLiss { get; } = All(new JsonObject()).Single(p => p.Key == "ToLiss");

    public static IReadOnlyList<XPlaneProfile> All(JsonObject config) =>
        Profiles.Merged(config).Where(kv => kv.Value is JsonObject o && Profiles.IsXPlane(o)).Select(kv => Parse(kv.Key, (JsonObject)kv.Value!)).ToList();

    /// <summary>The profile for an aircraft (its .acf path); null if there is none. The longest matching key wins.</summary>
    public static XPlaneProfile? Select(JsonObject config, string aircraftPath) =>
        aircraftPath.Length == 0 ? null : All(config).Where(p => p.Matches(aircraftPath)).OrderByDescending(p => p.Key.Length).FirstOrDefault();

    /// <summary>A display without a window title or command is left out (a hand-written profile may be half done).</summary>
    private static XPlaneProfile Parse(string key, JsonObject o)
    {
        var displays = new Dictionary<string, XPlaneDisplay>();
        foreach (var (name, node) in o["displays"] as JsonObject ?? [])
        {
            if (node is JsonObject d && d["title"].Text() is { Length: > 0 } title && d["command"].Text() is { Length: > 0 } command)
            {
                displays[name] = new XPlaneDisplay(title, command, (int)d["state"].Number(-1));
            }
        }

        var dim = o["popout_dimming"] as JsonObject;
        return new XPlaneProfile(key, displays, o["reinstate_command"].Text(), o["state_array"].Text(),
            Math.Max(0, (int)o["frame"].Number(0)), dim?["name"].Text() ?? "the aircraft dims its pop-outs itself");
    }
}
