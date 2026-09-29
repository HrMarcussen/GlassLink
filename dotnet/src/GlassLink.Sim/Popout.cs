using System.Text.Json;
using System.Text.Json.Nodes;
using GlassLink.Capture.Windows;
using GlassLink.Core.Config;

namespace GlassLink.Sim;

/// <summary>The sim's cockpit camera over SimConnect. No key presses: views and zoom are ordinary variables.</summary>
public sealed class SimCamera(SimConnectClient sim)
{
    public const int StateCockpit = 2;
    public static readonly (int Type, int Index) PilotView = (1, 1);

    private readonly SimVariable _title = sim.Text("TITLE");
    private readonly SimVariable _state = sim.Number("CAMERA STATE", "Enum");
    private readonly SimVariable _zoom = sim.Number("COCKPIT CAMERA ZOOM", "Percent");
    private readonly SimVariable _action = sim.Number("CAMERA REQUEST ACTION", "Enum");
    private readonly SimVariable _viewType = sim.Number("CAMERA VIEW TYPE AND INDEX:0", "Enum");
    private readonly SimVariable _viewIndex = sim.Number("CAMERA VIEW TYPE AND INDEX:1", "Enum");

    public bool Ready => sim.Connected && _state.Value is not null && _title.Text is not null;

    public string Title => _title.Text ?? "";

    public bool InCockpit => _state.Value is { } s && (int)s == StateCockpit;

    public double? Zoom => _zoom.Value;

    public (int Type, int Index)? View => _viewType.Value is { } t && _viewIndex.Value is { } i ? ((int)t, (int)i) : null;

    public void SetZoom(double percent) => sim.Set(_zoom, percent);

    public void SetView(int type, int index)
    {
        sim.Set(_viewType, type);
        sim.Set(_viewIndex, index);
    }

    /// <summary>Back to the view's default direction and position.</summary>
    public void Reset() => sim.Set(_action, 1);
}

public sealed record PopoutSettings(double GraceSeconds, double RetrySeconds, int MaxAttempts, string? RestoreKey, bool Auto)
{
    public static PopoutSettings From(JsonObject config)
    {
        var p = config["popout"] as JsonObject;
        double Num(string key, double fallback) => p?[key] is { } n && n.GetValueKind() == JsonValueKind.Number ? n.AsDouble() : fallback;
        var key = p?["camera_restore_key"] is { } k && k.GetValueKind() == JsonValueKind.String ? k.GetValue<string>() : null;
        return new PopoutSettings(Num("grace_s", 10), Num("retry_s", 60), (int)Num("max_attempts", 2), string.IsNullOrWhiteSpace(key) ? null : key,
            p?["auto"] is { } a && a.GetValueKind() == JsonValueKind.True);
    }
}

/// <summary>
/// Pops displays out of the cockpit: puts the camera where the click points were recorded, Right-Alt + clicks each
/// display, names the window that appears (GlassLink:pfd), sizes and parks it, and gives the user their view back.
/// </summary>
public sealed class PopoutProcedure(ConfigFile config, SimCamera camera, Action<string> say)
{
    public const string SimProcess = "FlightSimulator2024.exe";
    public const string SimClass = "AceApp";
    public const string SimTitle = "Microsoft Flight Simulator";
    public const string TitlePrefix = "GlassLink:";

    public static WindowInfo? SimMainWindow() =>
        WindowFinder.Enumerate().Where(w => w.ClassName == SimClass && w.Title.StartsWith(SimTitle, StringComparison.Ordinal))
            .OrderByDescending(w => (long)w.Client.Width * w.Client.Height).FirstOrDefault();

    public static HashSet<nint> SimWindows() =>
        WindowFinder.Enumerate().Where(w => w.ClassName == SimClass && string.Equals(w.Process, SimProcess, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Handle).ToHashSet();

    /// <summary>Pop-outs that GlassLink did not make. A display that is already popped out cannot be popped out again.</summary>
    public static List<WindowInfo> StrayPopouts()
    {
        var main = SimMainWindow();
        return WindowFinder.Enumerate().Where(w => w.ClassName == SimClass && string.Equals(w.Process, SimProcess, StringComparison.OrdinalIgnoreCase)
            && w.Handle != main?.Handle && !w.Title.StartsWith(TitlePrefix, StringComparison.Ordinal) && !w.Title.StartsWith(SimTitle, StringComparison.Ordinal)).ToList();
    }

    /// <summary>Pops out the named displays. Returns the ones that got a window.</summary>
    /// <summary>Asked before every camera move and click: true = stop, restore the camera, return (the DMC is quitting).</summary>
    public Func<bool> Stop { get; init; } = () => false;

    public IReadOnlyList<string> Run(IReadOnlyList<string> names, AircraftProfile profile)
    {
        var done = new List<string>();
        if (SimMainWindow() is not { } sim)
        {
            say("the simulator window was not found");
            return done;
        }

        var (oldView, oldZoom) = (camera.View, camera.Zoom);
        say($"using profile '{profile.Key}', camera was view {oldView} zoom {oldZoom:0}");
        try
        {
            foreach (var group in names.Where(profile.Points.ContainsKey).GroupBy(n => profile.Points[n].Camera.Key))
            {
                if (Stop())
                {
                    break;
                }

                ApplyCamera(profile.Points[group.First()].Camera, profile.Zoom, sim.Handle);
                sim = SimMainWindow() ?? sim;
                if (group.Key == "reset" && profile.Detect == "pfd_sphere" && profile.Points.TryGetValue("pfd", out var pfd) && !ViewMatches(sim, pfd))
                {
                    // Clicking now would pop out the wrong instruments (seen 18 Sept 2026: PFD -> ND, ND -> standby horizon).
                    say("the view still does not match the profile; not clicking. Retrying later.");
                    continue;
                }

                foreach (var name in group)
                {
                    if (Stop())
                    {
                        break;
                    }

                    if (Click(name, profile.Points[name], sim))
                    {
                        done.Add(name);
                    }
                }
            }
        }
        finally
        {
            Restore(oldView, oldZoom, sim.Handle);
        }

        return done;
    }

    /// <summary>Puts the camera into the state a point was recorded in and waits until the picture has stopped moving.</summary>
    public void ApplyCamera(CameraSpec spec, double zoom, nint simWindow)
    {
        if (spec.ViewType is null)
        {
            say($"camera: pilot seat, reset, zoom {zoom:0}");
            if (camera.View is { } v && v != SimCamera.PilotView)
            {
                camera.SetView(SimCamera.PilotView.Type, SimCamera.PilotView.Index);
                Thread.Sleep(1000);
            }
        }
        else
        {
            say($"camera: view {spec.ViewType}/{spec.ViewIndex}, reset, zoom {zoom:0}");
            camera.SetView(spec.ViewType.Value, spec.ViewIndex!.Value);
            Thread.Sleep(1000);
        }

        camera.Reset();                                  // the user may have looked around in that view earlier
        Thread.Sleep(800);
        camera.SetZoom(zoom);
        Thread.Sleep(1200);
        WaitUntilStill(simWindow);
    }

    /// <summary>
    /// If the PFD is lit, it must be where the profile expects it. A dark cockpit gives nothing to check: then the
    /// profile's points are trusted (true). A PFD somewhere else means the camera is not in the calibrated view yet;
    /// four looks, 1.5 s apart, before giving up (false).
    /// </summary>
    private bool ViewMatches(WindowInfo sim, ClickPoint pfd)
    {
        var (expectedX, expectedY) = (sim.Client.Left + pfd.X * sim.Client.Width, sim.Client.Top + pfd.Y * sim.Client.Height);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            if (WindowFinder.Grab(sim.Handle) is not { } grab || PfdDetector.Find(grab.Pixels, grab.Width, grab.Height, grab.Width * 4) is not { } found)
            {
                say("displays not detected (dark cockpit?); using the profile's points");
                return true;
            }

            var (x, y) = (sim.Window.Left + found.X, sim.Window.Top + found.Y);
            if (Math.Abs(x - expectedX) < sim.Client.Width * 0.06 && Math.Abs(y - expectedY) < sim.Client.Height * 0.08)
            {
                say($"PFD seen at ({x}, {y}), as the profile expects");
                return true;
            }

            say($"the PFD is visible at ({x}, {y}) but the profile expects ({expectedX:0}, {expectedY:0}): the view is not the calibrated one yet, waiting ({attempt}/4)");
            Thread.Sleep(1500);
        }

        return false;
    }

    /// <summary>Camera moves are animated and can take seconds (right after loading, much longer). Two quiet
    /// comparisons of the sim's picture in a row count as still.</summary>
    public bool WaitUntilStill(nint simWindow, double maxSeconds = 12, double threshold = 2.0)
    {
        var started = Environment.TickCount64;
        var previous = WindowFinder.CoarseGrey(simWindow);
        var quiet = 0;
        while (Environment.TickCount64 - started < maxSeconds * 1000)
        {
            Thread.Sleep(400);
            var current = WindowFinder.CoarseGrey(simWindow);
            if (previous is not null && current is not null && previous.Length == current.Length)
            {
                var motion = 0.0;
                for (var i = 0; i < current.Length; i++)
                {
                    motion += Math.Abs(current[i] - previous[i]);
                }

                quiet = motion / current.Length < threshold ? quiet + 1 : 0;
                if (quiet >= 2)
                {
                    var waited = (Environment.TickCount64 - started) / 1000.0;
                    if (waited > 1.5)
                    {
                        say($"camera settled after {waited:0.0} s");
                    }

                    return true;
                }
            }

            previous = current;
        }

        say($"camera still moving after {maxSeconds:0} s; continuing anyway");
        return false;
    }

    private bool Click(string name, ClickPoint point, WindowInfo sim)
    {
        var x = (int)Math.Round(sim.Client.Left + point.X * sim.Client.Width);
        var y = (int)Math.Round(sim.Client.Top + point.Y * sim.Client.Height);
        var before = SimWindows();
        say($"{name}: Right-Alt + click at ({x}, {y})");
        if (!Input.RightAltClick(sim.Handle, x, y))
        {
            say("could not bring the sim window to the front; not clicking");
            return false;
        }

        var appeared = new HashSet<nint>();
        for (var i = 0; i < 30 && appeared.Count == 0; i++)
        {
            Thread.Sleep(500);
            appeared = SimWindows();
            appeared.ExceptWith(before);
        }

        if (appeared.Count != 1)
        {
            say($"{name}: expected one new window, got {appeared.Count}");
            return false;
        }

        Thread.Sleep(1000);
        Adopt(appeared.First(), name);
        Thread.Sleep(1000);
        return true;
    }

    /// <summary>Makes a fresh pop-out window the display's window: name, size, parking position.</summary>
    public void Adopt(nint hwnd, string name)
    {
        WindowFinder.SetTitle(hwnd, TitlePrefix + name);
        JsonObject? display = null;
        config.Update(root =>
        {
            display = ConfigFile.Section(ConfigFile.Section(root, "displays"), name);
            display["match"] = new JsonObject { ["process"] = SimProcess, ["class"] = SimClass, ["title"] = TitlePrefix + name };
        });
        if (WindowFinder.Describe(hwnd) is not { } w)
        {
            return;
        }

        var (size, position) = (Pair(display?["client_size"]), Pair(display?["position"]));
        if (size is { } s)
        {
            WindowFinder.SetClientSize(w, s.A, s.B, position?.A, position?.B);
        }
        else if (position is { } p)
        {
            WindowFinder.Move(hwnd, p.A, p.B);
        }

        WindowFinder.SetNoActivate(hwnd);
        var now = WindowFinder.Describe(hwnd);
        say($"{name}: 0x{hwnd:x} client {now?.Client.Width}x{now?.Client.Height} at {now?.Window.Left},{now?.Window.Top}");
    }

    /// <summary>Back to the seat view and zoom the user had, then their own flying view if they configured its key.</summary>
    public void Restore((int Type, int Index)? view, double? zoom, nint simWindow)
    {
        if (view is { } v && camera.View != v)
        {
            camera.SetView(v.Type, v.Index);
            Thread.Sleep(1000);
        }

        camera.Reset();
        if (zoom is { } z)
        {
            Thread.Sleep(500);
            camera.SetZoom(z);
        }

        var key = PopoutSettings.From(config.Snapshot()).RestoreKey;
        if (key is not null)
        {
            Thread.Sleep(800);
            say($"camera: back to your view with {key}");
            try
            {
                Input.SendCombo(simWindow, key);
            }
            catch (FormatException ex)
            {
                say($"camera_restore_key: {ex.Message}");
            }
        }
        else
        {
            Input.BringToFront(simWindow);               // the keyboard focus belongs on the sim's main window
        }
    }

    private static (int A, int B)? Pair(JsonNode? node) =>
        node is JsonArray { Count: 2 } a ? ((int)a[0]!.AsDouble(), (int)a[1]!.AsDouble()) : null;
}
