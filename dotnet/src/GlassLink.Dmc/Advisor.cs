using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GlassLink.Dmc;

public sealed record Advice(string Id, string Level, string Tab, string Title, string Detail, IReadOnlyList<string> Steps);

public sealed record GpuFacts(IReadOnlyList<string> Gpus, bool Amd, bool Nvidia, bool AmdFrameGeneration, bool AmdChill, bool AmdFrameLimit);

public sealed record SimFacts(int? GlassRefresh, string? FrameGeneration);

/// <summary>What the advisor looks at; the API fills it from the running DMC, tests fill it by hand.</summary>
public sealed record AdvisorInput(
    IReadOnlyList<(string Name, long Received, bool InUse, bool HasWindow, double CaptureFps, bool SimDisplay)> Displays,
    IReadOnlyList<(string Serial, string Label, bool Alive, string Display, bool Outdated, IReadOnlyList<string> Health)> Dus,
    int Strays, string PopoutStatus, string PopoutDetail, IReadOnlyList<string> PopoutMissing,
    bool BrightnessRunning, bool BrightnessMapEmpty, string? BrightnessStanddown, string Aircraft);

/// <summary>
/// Turns what the DMC can observe into troubleshooting steps a builder can act on. Rules fire on evidence only, and
/// the steps are tailored with facts read (never changed) from the PC: the graphics card brand, whether the AMD
/// driver's frame generation is on, the sim's glass cockpit refresh rate. Deliberately not here: the stability of a
/// particular PC. Port of glasslink/advisor.py.
/// </summary>
public sealed class Advisor(Func<GpuFacts>? gpuFacts = null, Func<SimFacts>? simFacts = null, Func<long>? clock = null)
{
    public const double SlowFps = 16, SlowForSeconds = 8;
    private static readonly string[] GlassNames = ["Low", "Medium", "High"];
    private readonly Func<GpuFacts> _gpuFacts = gpuFacts ?? ReadGpuFacts;
    private readonly Func<SimFacts> _simFacts = simFacts ?? ReadSimFacts;
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly Dictionary<string, (long Received, long At)> _previous = [];
    private readonly Dictionary<string, long> _slowSince = [];
    private (GpuFacts Gpu, SimFacts Sim)? _facts;
    private long _factsAt = long.MinValue / 2;

    /// <summary>Frames per second the sim delivers to each display's window.</summary>
    public Dictionary<string, double> Rates { get; } = [];

    /// <summary>A copy of the rates, safe to read while another request updates them.</summary>
    public IReadOnlyDictionary<string, double> RatesNow()
    {
        lock (_previous)
        {
            return new Dictionary<string, double>(Rates);
        }
    }

    public IReadOnlyList<Advice> Advise(AdvisorInput input)
    {
        lock (_previous)
        {
            var result = new List<Advice>();
            var slow = Track(input);
            if (slow.Count > 0)
            {
                var (gpu, sim) = Facts();
                result.Add(new Advice("slow_source", "warn", "displays",
                    $"The sim delivers only {slow.Min(n => Rates[n]):0} frames a second to the displays",
                    $"Frames per second received: {string.Join(", ", slow.Select(n => $"{n} {Rates[n]:0}"))}. A DU cannot show more than the sim hands to its " +
                    "pop-out window; the aircraft's displays normally change about 20 times a second.",
                    SlowSourceSteps(gpu, sim)));
            }

            foreach (var du in input.Dus.Where(d => d.Alive))
            {
                var who = du.Label.Length > 0 ? du.Label : du.Serial[..Math.Min(8, du.Serial.Length)];
                if (du.Health.Count > 0 && du.Display.Length > 0)
                {
                    result.Add(new Advice($"du_behind:{du.Serial}", "warn", "dus", $"{who} is not keeping up with its display", string.Join("; ", du.Health),
                    [
                        "Plug the DU into a USB 2.0 high-speed port or hub of its own; avoid sharing a hub with webcams, audio or storage.",
                        "Try another cable: a charge-only or very long cable falls back to a slow link.",
                        "Display units tab: a transfer time (rx) above 15 ms points at the USB link, a decode time above 30 ms at the DU itself (restart it, then update its firmware).",
                    ]));
                }

                if (du.Outdated)
                {
                    result.Add(new Advice($"fw:{du.Serial}", "info", "dus", $"{who} has older firmware", "It keeps working, but fixes and new features need the update.",
                        ["Display units tab: press Update on that DU. It takes about ten seconds and the DU restarts itself."]));
                }

                if (du.Display.Length == 0)
                {
                    result.Add(new Advice($"unassigned:{du.Serial}", "info", "dus", $"{who} is connected but shows nothing", "No display is assigned to it.",
                        ["Display units tab: press Identify to see which panel it is, then choose its display."]));
                }
            }

            if (input.Strays > 0)
            {
                result.Add(new Advice("strays", "warn", "setup", $"{input.Strays} pop-out window(s) were not made by GlassLink",
                    "The sim opens each display only once, so a display popped out by hand or by another tool cannot be popped out or learned again. They are often parked off-screen and forgotten.",
                    ["Setup tab: press Close them. GlassLink then pops the missing displays out itself."]));
            }

            if (input.PopoutStatus == "gave_up")
            {
                result.Add(new Advice("popout_gave_up", "warn", "setup", $"Could not pop out: {string.Join(", ", input.PopoutMissing)}",
                    "The click opened no window twice, so GlassLink stopped moving your camera for it.",
                [
                    "Setup tab: if a banner lists pop-out windows not made by GlassLink, close them first.",
                    "Press Learn (captain seat), or Learn (FO seat) for an FO side display, and Right-Alt + click the display once when the banner says so.",
                    "Press Close window on that display to make GlassLink try again with the learned point.",
                ]));
            }
            else if (input.PopoutDetail.Contains("no pop-out profile") || input.PopoutDetail.Contains("no click point"))
            {
                result.Add(new Advice("popout_unlearned", "info", "setup", "GlassLink does not know where some displays are in this aircraft", input.PopoutDetail,
                    ["Setup tab: press Learn on each display without a click point and Right-Alt + click it once. The point is stored per aircraft; afterwards the displays pop out by themselves."]));
            }

            if (input.BrightnessRunning && input.BrightnessStanddown is null && input.BrightnessMapEmpty && input.Aircraft.Length > 0)
            {
                result.Add(new Advice("no_brightness_profile", "info", "system", "The cockpit brightness knobs are not linked for this aircraft",
                    $"No brightness profile for '{input.Aircraft}'. The DUs use their own brightness slider only.",
                    ["Nothing to do for flying. A profile maps each display to the aircraft's brightness variable; see popout.profiles in the configuration."]));
            }

            return result;
        }
    }

    /// <summary>Steps for "the sim delivers too few frames to a pop-out", most likely cause first.</summary>
    public static IReadOnlyList<string> SlowSourceSteps(GpuFacts gpu, SimFacts sim)
    {
        var steps = new List<string>();
        if (gpu.AmdFrameGeneration)
        {
            steps.Add("AMD Fluid Motion Frames is switched ON in your graphics driver, and that is the usual cause: it only serves the window in focus and leaves " +
                      "pop-outs with about 13 frames a second. AMD Software > Gaming > Graphics: set the global preset to Default (not HYPR-RX), or turn AMD Fluid Motion Frames off for the sim.");
        }
        else if (gpu.Amd)
        {
            steps.Add("AMD Software > Gaming > Graphics: check that the preset is Default, not HYPR-RX, and that AMD Fluid Motion Frames is off for the sim. " +
                      "Driver frame generation leaves pop-outs with about 13 frames a second.");
        }

        if (gpu.AmdChill || gpu.AmdFrameLimit)
        {
            steps.Add("A frame rate limit is active in the AMD driver (Radeon Chill or Frame Rate Target). The sim shares a limit between all its windows: switch it off for the sim.");
        }

        if (gpu.Nvidia)
        {
            steps.Add("NVIDIA app / Control Panel, settings for the sim: Smooth Motion off, Max Frame Rate off. Driver frame generation and driver frame limits starve the pop-out windows.");
        }

        if (!gpu.Amd && !gpu.Nvidia)
        {
            steps.Add("Graphics driver: switch off driver-level frame generation and any frame rate limit for the sim.");
        }

        steps.Add(sim.GlassRefresh switch
        {
            < 2 and var g => $"Sim > Options > General > Graphics: Glass cockpit refresh rate is {GlassNames[Math.Clamp(g, 0, 2)]}. That is fine with a fast sim, but with the sim " +
                             "below about 40 fps it leaves the instruments under 20 updates a second: set it to High.",
            null => "Sim > Options > General > Graphics: set Glass cockpit refresh rate to High.",
            _ => "",
        });
        steps.Add("Check the sim's own frame rate (developer mode FPS counter, or the System tab): a pop-out cannot be faster than the sim. Below about 20 fps, lower the graphics settings.");
        steps.Add("Judge the result with the sim window in focus: the rate is often fine while another program is in front.");
        return steps.Where(s => s.Length > 0).ToList();
    }

    private List<string> Track(AdvisorInput input)
    {
        var now = _clock();
        var slow = new List<string>();
        foreach (var d in input.Displays)
        {
            if (!_previous.TryGetValue(d.Name, out var prev) || d.Received < prev.Received)
            {
                _previous[d.Name] = (d.Received, now);
                continue;
            }

            if (now - prev.At >= 1500)
            {
                Rates[d.Name] = (d.Received - prev.Received) * 1000.0 / (now - prev.At);
                _previous[d.Name] = (d.Received, now);
            }

            // "The sim delivers too few frames" is only said about windows of the sim: a test pattern may be slow.
            var watched = d.SimDisplay && d.InUse && d.HasWindow && d.CaptureFps > SlowFps;
            if (watched && Rates.TryGetValue(d.Name, out var rate) && rate < SlowFps)
            {
                _slowSince.TryAdd(d.Name, now);
                if (now - _slowSince[d.Name] >= SlowForSeconds * 1000)
                {
                    slow.Add(d.Name);
                }
            }
            else
            {
                _slowSince.Remove(d.Name);
            }
        }

        foreach (var gone in _previous.Keys.Where(n => input.Displays.All(d => d.Name != n)).ToList())
        {
            _previous.Remove(gone);
            Rates.Remove(gone);
            _slowSince.Remove(gone);
        }

        return slow;
    }

    private (GpuFacts Gpu, SimFacts Sim) Facts()
    {
        var now = _clock();
        if (_facts is null || now - _factsAt > 30_000)
        {
            try
            {
                _facts = (_gpuFacts(), _simFacts());
            }
            catch (Exception)                                // advice must never break the status page
            {
                _facts = (new GpuFacts([], false, false, false, false, false), new SimFacts(null, null));
            }

            _factsAt = now;
        }

        return _facts.Value;
    }

    /// <summary>Graphics cards and the AMD driver switches that matter for pop-outs. Read-only registry access.</summary>
    public static GpuFacts ReadGpuFacts()
    {
        var gpus = new List<string>();
        bool amd = false, nvidia = false, frameGen = false, chill = false, limit = false;
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        foreach (var name in cls?.GetSubKeyNames().Where(n => Regex.IsMatch(n, @"^\d{4}$")) ?? [])
        {
            try
            {
                using var key = cls!.OpenSubKey(name);
                var description = key?.GetValue("DriverDesc") as string ?? "";
                if (description.Length == 0 || description.Contains("Basic"))
                {
                    continue;
                }

                gpus.Add(description);
                if (Regex.IsMatch(description, "AMD|Radeon", RegexOptions.IgnoreCase))
                {
                    amd = true;
                    frameGen |= Flag(key!.GetValue("DrvFrameGenEnabled"));
                    chill |= Flag(key.GetValue("KMD_ChillEnabled"));
                    limit |= Flag(key.GetValue("KMD_FRTEnabled"));
                }

                nvidia |= Regex.IsMatch(description, "NVIDIA|GeForce", RegexOptions.IgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        return new GpuFacts(gpus, amd, nvidia, frameGen, chill, limit);

        static bool Flag(object? value) => value switch
        {
            byte[] { Length: > 0 } bytes => bytes[0] != 0,
            int number => number != 0,
            string text => int.TryParse(text, out var n) && n != 0,
            _ => false,
        };
    }

    /// <summary>The sim's own graphics options, from the newest UserCfg.opt (the sim rewrites it when Apply is pressed).</summary>
    public static SimFacts ReadSimFacts()
    {
        string[] candidates =
        [
            @"%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt",
            @"%APPDATA%\Microsoft Flight Simulator 2024\UserCfg.opt",
            @"%LOCALAPPDATA%\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\LocalCache\UserCfg.opt",
            @"%APPDATA%\Microsoft Flight Simulator\UserCfg.opt",
        ];
        var path = candidates.Select(Environment.ExpandEnvironmentVariables).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return path is null ? new SimFacts(null, null) : ParseSimFacts(File.ReadAllText(path));
    }

    public static SimFacts ParseSimFacts(string text)
    {
        var glass = Regex.Match(text, @"\{GlassCockpitsRefreshRate\s+Quality\s+(\d+)");       // first block = desktop, second = VR
        var frameGen = Regex.Match(text, @"^\s*FrameGeneration\s+(\S+)", RegexOptions.Multiline);
        return new SimFacts(glass.Success ? int.Parse(glass.Groups[1].Value) : null, frameGen.Success ? frameGen.Groups[1].Value : null);
    }
}
