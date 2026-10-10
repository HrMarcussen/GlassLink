using GlassLink.Capture;
using GlassLink.Capture.Windows;
using GlassLink.Core.Config;

namespace GlassLink.Sim;

/// <summary>How a sim stands, for the top bar: whether it is there at all, whether all is well, and in words.</summary>
public sealed record SimSummary(bool Running, bool Ok, string Text);

/// <summary>
/// What the DMC needs from a simulator, whichever one runs: MSFS (SimConnect; displays popped out by clicks, the DMC
/// dims the DUs with the cockpit knobs) or X-Plane (its web API; the aircraft opens and dims its own pop-outs). The
/// DMC asks the active one instead of asking which sim it is.
/// </summary>
public interface ISimulator
{
    /// <summary>"msfs" or "xplane".</summary>
    string Id { get; }

    /// <summary>"MSFS" or "X-Plane", as the user reads it.</summary>
    string Name { get; }

    bool Connected { get; }

    /// <summary>The sim's frame rate, if it says.</summary>
    double? Fps { get; }

    /// <summary>The loaded aircraft's name ("" without one).</summary>
    string Aircraft { get; }

    /// <summary>In a flight, in the cockpit: where displays can be popped out.</summary>
    bool InCockpit { get; }

    /// <summary>The key of the aircraft's pop-out profile; null without one.</summary>
    string? ProfileKey { get; }

    /// <summary>The automatic pop-out's state; null before the DMC started it.</summary>
    AutoPopoutState? PopoutState { get; }

    SimSummary Summary();

    /// <summary>Closes a display's pop-out window, the way this sim allows; false if it could not.</summary>
    bool ClosePopout(string display, WindowInfo window);

    /// <summary>The cockpit knob for a display's DU, 0..1; null when the aircraft dims its pop-outs itself or it is not known.</summary>
    double? Brightness(string display);

    BrightnessStatus BrightnessStatus();

    /// <summary>Another window a display may be in this sim (X-Plane: the profile's pop-out); null for none.</summary>
    WindowRule? Rule(string display);
}

/// <summary>MSFS 2024 through SimConnect: pop-outs by camera moves and clicks, brightness from the cockpit knobs.</summary>
public sealed class MsfsSim(SimConnectClient sim, SimCamera camera, BrightnessLink brightness, ConfigFile config, Func<AutoPopout?> popout) : ISimulator
{
    public string Id => "msfs";

    public string Name => "MSFS";

    public bool Connected => sim.Connected;

    public double? Fps => sim.SimFps;

    public string Aircraft => camera.Title;

    public bool InCockpit => camera.InCockpit;

    public string? ProfileKey => Profiles.Select(config.Snapshot(), camera.Title)?.Key;

    public AutoPopoutState? PopoutState => popout()?.State;

    public SimSummary Summary() =>
        PopoutProcedure.SimMainWindow() is null ? new(false, false, "Sim not running")   // general: which sim comes next is not known
        : !sim.Connected ? new(true, false, "MSFS starting")
        : !camera.InCockpit ? new(true, false, "MSFS: not in cockpit")
        : new(true, true, $"MSFS: {camera.Title}");

    public bool ClosePopout(string display, WindowInfo window) => WindowFinder.Close(window.Handle);

    public double? Brightness(string display) => brightness.For(display);

    public BrightnessStatus BrightnessStatus() => brightness.Status();

    public WindowRule? Rule(string display) => null;          // the configured window (GlassLink:<name>) is the one
}

/// <summary>X-Plane 12 through its web API: the aircraft's profile opens its pop-outs by command; they dim themselves.</summary>
public sealed class XPlaneSim(XPlaneClient xplane, ConfigFile config, Func<XPlanePopout?> popout) : ISimulator
{
    private (string Path, long At, XPlaneProfile? Profile) _cached = ("", long.MinValue / 2, null);

    public string Id => "xplane";

    public string Name => "X-Plane";

    public bool Connected => xplane.Connected;

    public double? Fps => xplane.Fps;

    public string Aircraft => xplane.AircraftName;

    public bool InCockpit => xplane.AircraftPath.Length > 0;

    /// <summary>The loaded aircraft's profile (built-in or from config.json); looked up again after 2 s, as the displays
    /// ask for it every second while one has no window.</summary>
    public XPlaneProfile? Profile
    {
        get
        {
            if (!xplane.Connected)
            {
                return null;
            }

            var (path, now) = (xplane.AircraftPath, Environment.TickCount64);
            var cached = _cached;
            if (cached.Path != path || now - cached.At > 2000)
            {
                _cached = cached = (path, now, XPlaneProfiles.Select(config.Snapshot(), path));
            }

            return cached.Profile;
        }
    }

    public string? ProfileKey => Profile?.Key;

    public AutoPopoutState? PopoutState => popout()?.State;

    public SimSummary Summary() =>
        !xplane.Connected ? new(false, false, "Sim not running")
        : xplane.AircraftPath.Length == 0 ? new(true, false, "X-Plane: no aircraft loaded")
        : Profile is null ? new(true, false, $"X-Plane: no profile for {xplane.AircraftName}")
        : new(true, true, $"X-Plane: {xplane.AircraftName}");

    /// <summary>With the aircraft's own command: X-Plane takes a close message to any of its windows as "quit X-Plane".</summary>
    public bool ClosePopout(string display, WindowInfo window) =>
        Profile?.Displays.GetValueOrDefault(display) is { } d && xplane.Command(d.Command);

    /// <summary>X-Plane's aircraft dim their pop-outs themselves (the ToLiss does, measured 2 Oct 2026): nothing to add on the DU.</summary>
    public double? Brightness(string display) => null;

    public BrightnessStatus BrightnessStatus() =>
        new(true, xplane.AircraftName, new Dictionary<string, string>(), Profile?.DimmingName ?? "X-Plane: the aircraft's pop-outs show its own brightness");

    public WindowRule? Rule(string display) => Profile?.Rule(display);
}
