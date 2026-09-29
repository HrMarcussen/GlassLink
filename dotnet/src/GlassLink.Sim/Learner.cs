using System.Text.Json.Nodes;
using GlassLink.Capture.Windows;
using GlassLink.Core.Config;

namespace GlassLink.Sim;

public sealed record LearnState(string Status, string? Display, string Detail, double[]? Point)
{
    public static readonly LearnState Idle = new("idle", null, "", null);

    public bool Busy => Status is "preparing" or "waiting" or "adopting" or "restoring";
}

/// <summary>
/// Learns a display's pop-out click point by watching the user pop it out once.
///   preparing  the camera is put into the view the automatic pop-out will use later: the aircraft profile's pilot
///              seat view, or the sim's copilot seat view for displays out of reach from the left seat
///   waiting    the user Right-Alt + clicks the display, as they would by hand
///   adopting   the point goes into the aircraft's profile (fractions of the sim's client area), the new window
///              becomes the display's window (named, sized, parked)
///   done       the camera is back where it was; capture of the new window starts by itself
/// </summary>
public sealed class Learner(ConfigFile config, SimCamera camera, Action<string> log)
{
    public const int WaitForClickSeconds = 90;
    private const ushort VkRightAlt = 0xA5, VkLeftButton = 0x01;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;

    public LearnState State { get; private set; } = LearnState.Idle;

    /// <summary>Told when learning starts and ends, so the automatic pop-out leaves the camera alone meanwhile.</summary>
    public Action<bool> PauseAuto { get; set; } = _ => { };

    /// <param name="view">"standard" = the profile's pilot seat view, "copilot" = the sim's copilot seat view.</param>
    public void Start(string display, string view = "standard")
    {
        lock (_gate)
        {
            if (view is not ("standard" or "copilot"))
            {
                throw new InvalidOperationException("view must be 'standard' or 'copilot'");
            }

            if (State.Busy)
            {
                throw new InvalidOperationException($"already learning '{State.Display}'");
            }

            if (!config.Read(root => (root["displays"] as JsonObject)?.ContainsKey(display) == true))
            {
                throw new InvalidOperationException($"unknown display '{display}'");
            }

            if (PopoutProcedure.SimMainWindow() is null)
            {
                throw new InvalidOperationException("the simulator is not running");
            }

            if (!CameraLock.TryEnter("learning a click point"))
            {
                throw new InvalidOperationException($"the camera is busy ({CameraLock.Owner}): try again in a moment");
            }

            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
            State = new LearnState("preparing", display, "setting the camera", null);
            var token = _cancel.Token;
            _thread = new Thread(() => Run(display, view, token)) { IsBackground = true, Name = "popout-learn" };
            _thread.Start();
        }
    }

    private Thread? _thread;

    /// <summary>Cancels a running Learn and waits (bounded) until the user's view is back: for the DMC quitting (#39).</summary>
    public void CancelAndWait(int timeoutMs = 15_000)
    {
        Cancel();
        _thread?.Join(timeoutMs);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cancel?.Cancel();
        }
    }

    private void Say(string text)
    {
        log($"learn: {text}");
        State = State with { Detail = text };
    }

    private void Run(string display, string view, CancellationToken cancel)
    {
        var procedure = new PopoutProcedure(config, camera, Say);
        ((int Type, int Index)? View, double? Zoom, nint Sim)? restore = null;
        LearnState? result = null;                           // shown only when the user's view is back (#38)
        PauseAuto(true);
        try
        {
            if (PopoutProcedure.SimMainWindow() is not { } sim || !camera.Ready)
            {
                throw new InvalidOperationException("SimConnect is not available (is the sim in a flight?)");
            }

            if (!camera.InCockpit)
            {
                throw new InvalidOperationException("the sim is not in the cockpit view");
            }

            var title = camera.Title;
            var profile = Profiles.Select(config.Snapshot(), title);
            var key = profile?.Key ?? (title.Trim().Length > 0 ? title.Trim() : "aircraft");     // first display of an unknown aircraft
            var zoom = profile?.Zoom ?? PopoutDefaultZoom();
            var spec = view == "copilot" ? CameraSpec.Copilot : CameraSpec.PilotReset;
            restore = (camera.View, camera.Zoom, sim.Handle);
            procedure.ApplyCamera(spec, zoom, sim.Handle);
            sim = PopoutProcedure.SimMainWindow() ?? sim;

            if (WindowFinder.Find(new WindowMatch(PopoutProcedure.SimProcess, null, null, PopoutProcedure.TitlePrefix + display, null, null)) is { } open)
            {
                WindowFinder.Close(open.Handle);             // learning again must not leave a duplicate behind
                Thread.Sleep(1000);
                Say($"closed the previous {display} window");
            }

            var before = PopoutProcedure.SimWindows();
            var strays = PopoutProcedure.StrayPopouts().Count;
            var note = strays == 0 ? "" : $" - note: {strays} pop-out window(s) not made by GlassLink are open; if one of them is this display, your click opens nothing: cancel and close them first";
            State = new LearnState("waiting", display, $"Right-Alt + click the {display} display in the cockpit now ({WaitForClickSeconds} s){note}", null);
            var (hwnd, click) = WatchForPopout(before, cancel);
            if (hwnd == 0)
            {
                result = cancel.IsCancellationRequested
                    ? new LearnState("cancelled", display, "cancelled", null)
                    : new LearnState("timeout", display, "no new pop-out window appeared" + (strays > 0 ? " (a pop-out not made by GlassLink is open: close it under Setup and try again)" : ""), null);
                return;
            }

            State = new LearnState("adopting", display, "storing the click point and parking the window", null);
            if (Normalise(click, sim.Client) is not { } point)
            {
                throw new InvalidOperationException("the click was outside the simulator window; nothing stored");
            }

            config.Update(root =>
            {
                var target = ConfigFile.Section(ConfigFile.Section(ConfigFile.Section(root, "popout"), "profiles"), key);
                var points = ConfigFile.Section(target, "points");
                points[display] = spec.ViewType is null
                    ? new JsonArray(point[0], point[1])
                    : new JsonObject { ["xy"] = new JsonArray(point[0], point[1]), ["camera"] = spec.ToJson() };
                target["zoom"] ??= zoom;
            });
            procedure.Adopt(hwnd, display);
            result = new LearnState("done", display, $"learned {display} at [{point[0]}, {point[1]}] for profile '{key}'; the window is parked", point);
            log($"learn: {result.Detail}");
        }
        catch (InvalidOperationException ex)
        {
            result = new LearnState("error", display, ex.Message, null);
        }
        catch (Exception ex)
        {
            result = new LearnState("error", display, $"{ex.GetType().Name}: {ex.Message}", null);
            log($"learn: {result.Detail}");
        }
        finally
        {
            if (restore is { } r)
            {
                State = new LearnState("restoring", display, "bringing your view back", State.Point);
                try
                {
                    // restore messages go to the log only: they must not replace the result (#38)
                    new PopoutProcedure(config, camera, m => log($"learn: {m}")).Restore(r.View, r.Zoom, r.Sim);
                }
                catch (Exception ex)
                {
                    log($"learn: could not restore the camera: {ex.Message}");
                }
            }

            State = result ?? new LearnState("error", display, "stopped", null);
            PauseAuto(false);
            CameraLock.Exit();                               // only now may the next Learn or pop-out move the camera
        }
    }

    /// <summary>Waits for a new pop-out window. The click position is where the cursor was while Right-Alt and the left
    /// button were down; if that press was too short to be seen, where the cursor was when the window appeared.</summary>
    private static (nint Window, (int X, int Y) Click) WatchForPopout(HashSet<nint> before, CancellationToken cancel)
    {
        (int X, int Y)? click = null;
        var deadline = Environment.TickCount64 + WaitForClickSeconds * 1000;
        var nextWindowCheck = 0L;
        while (Environment.TickCount64 < deadline && !cancel.IsCancellationRequested)
        {
            if (Input.IsDown(VkRightAlt) && Input.IsDown(VkLeftButton))
            {
                click = Input.Cursor;
            }

            if (Environment.TickCount64 >= nextWindowCheck)      // enumerating windows is the expensive part
            {
                nextWindowCheck = Environment.TickCount64 + 200;
                var now = PopoutProcedure.SimWindows();
                now.ExceptWith(before);
                if (now.Count > 0)
                {
                    return (now.Order().First(), click ?? Input.Cursor);
                }
            }

            Thread.Sleep(15);
        }

        return (0, click ?? default);
    }

    /// <summary>Screen point -> fractions of the sim's client area (4 decimals); null if it lies outside.</summary>
    public static double[]? Normalise((int X, int Y) point, Rect client)
    {
        var fx = (point.X - client.Left) / (double)Math.Max(1, client.Width);
        var fy = (point.Y - client.Top) / (double)Math.Max(1, client.Height);
        return fx is < 0 or > 1 || fy is < 0 or > 1 ? null : [Math.Round(fx, 4), Math.Round(fy, 4)];
    }

    private double PopoutDefaultZoom() =>
        config.Read(root => (root["popout"] as JsonObject)?["zoom"] is { } z && z.GetValueKind() == System.Text.Json.JsonValueKind.Number ? z.AsDouble() : 30);
}
