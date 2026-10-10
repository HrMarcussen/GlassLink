using System.Diagnostics;
using System.Text.Json.Nodes;
using GlassLink.Capture;
using GlassLink.Capture.Windows;
using GlassLink.Core.Config;
using GlassLink.Core.Du;
using GlassLink.Sim;

namespace GlassLink.Dmc;

/// <summary>Everything the DMC consists of, wired together: configuration, displays, DUs, the sim link, advice.</summary>
public sealed class DmcRuntime : IDisposable
{
    private readonly Queue<string> _logTail = new();

    public DmcRuntime(string configPath)
    {
        ConfigPath = Path.GetFullPath(configPath);
        Root = Path.GetDirectoryName(ConfigPath)!;
        Version = ReadText("VERSION") ?? "0.0.0";
        FirmwareVersion = ReadText("FIRMWARE_VERSION") ?? Version;
        Build = ReadText("BUILD") ?? GitDescribe(Root);        // a released copy carries its build id in a file; a checkout asks git
        Config = ConfigFile.Load(ConfigPath);

        WindowFinder.SetDpiAware();
        Process = TuneProcess();
        Sim = new SimConnectClient(log: Log);
        Camera = new SimCamera(Sim);
        Brightness = new BrightnessLink(Config, Sim, Camera);
        XPlane = new XPlaneClient(Log);
        MsfsSim = new MsfsSim(Sim, Camera, Brightness, Config, () => Auto);
        XPlaneSim = new XPlaneSim(XPlane, Config, () => XPlaneAuto);
        Displays = new DisplayRegistry(Config, name => Dus?.IsShown(name) == true, Log)
        {
            Alternative = name => ActiveSim.Rule(name),          // the same display names find the X-Plane aircraft's pop-outs
        };
        Dus = DuManager.ForWinUsb(Config, Displays.Slot, Log);
        Dus.BandFactory = (name, width, height, parts) => new BandComposer(name, width, height, parts);
        Dus.SimBrightness = display => BrightnessEnabled ? ActiveSim.Brightness(display) : null;
        // The size of the picture a display really publishes (after max_size); its configured client_size before the
        // first frame (#27).
        Dus.DisplaySize = display => Displays.Slot(display) is { Width: > 0, Height: > 0 } slot ? (slot.Width, slot.Height)
            : Config.Read<(int, int)?>(root => ((root["displays"] as JsonObject)?[display] as JsonObject)?["client_size"].Pair());
        Displays.Removed = Dus.DisplayRemoved;
        Learner = new Learner(Config, Camera, Log) { PauseAuto = paused => { if (Auto is not null) { Auto.Paused = paused; } } };
        Advisor = new Advisor();
        Updater = new Updater(Version, Config, Log);
    }

    public string ConfigPath { get; }

    public string Root { get; }

    public string Version { get; }

    public string FirmwareVersion { get; }

    public string Build { get; }

    public ConfigFile Config { get; }

    public (string Priority, int[] Affinity) Process { get; }

    public SimConnectClient Sim { get; }

    public SimCamera Camera { get; }

    public BrightnessLink Brightness { get; }

    public DisplayRegistry Displays { get; }

    public DuManager Dus { get; }

    public AutoPopout? Auto { get; private set; }

    public XPlaneClient XPlane { get; }

    public XPlanePopout? XPlaneAuto { get; private set; }

    public MsfsSim MsfsSim { get; }

    public XPlaneSim XPlaneSim { get; }

    /// <summary>The sim that runs: X-Plane while X-Plane answers, else MSFS (which also stands for "no sim yet").</summary>
    public ISimulator ActiveSim => XPlane.Connected ? XPlaneSim : MsfsSim;

    /// <summary>The automatic pop-out of the sim that runs.</summary>
    public AutoPopoutState? PopoutState => ActiveSim.PopoutState;

    /// <summary>Closes a display's pop-out so it is popped out afresh, the way the window's own sim allows (X-Plane would
    /// take a close message to the window as "quit X-Plane"). False if it could not be closed.</summary>
    public bool ClosePopout(string name, WindowInfo window)
    {
        ISimulator sim = string.Equals(window.Process, XPlaneClient.Process, StringComparison.OrdinalIgnoreCase) ? XPlaneSim : MsfsSim;
        if (!sim.ClosePopout(name, window))
        {
            return false;
        }

        RetryPopout(name);
        return true;
    }

    /// <summary>Forget earlier failed pop-outs (one display, or all) and try again, in whichever sim runs.</summary>
    public void RetryPopout(string? name = null)
    {
        Auto?.Retry(name);
        XPlaneAuto?.Retry(name);
    }

    public Learner Learner { get; }

    public Advisor Advisor { get; }

    public Updater Updater { get; }

    public DateTime Started { get; } = DateTime.UtcNow;

    public bool BrightnessEnabled => Config.Read(root => (root["brightness"] as JsonObject)?["enabled"] is not { } e || e.GetValueKind() != System.Text.Json.JsonValueKind.False);

    /// <summary>The DU firmware image to install: an installed copy's own; in a checkout the one named in the
    /// configuration, else the one built in this checkout, else one next to GlassLink.exe.</summary>
    /// <remarks>An installed copy takes only the image installed with it: its configuration folder can be written by any
    /// program the user runs, the install folder cannot (SECURITY.md). A checkout takes its own build, or firmware.image.</remarks>
    public string FirmwareImagePath => Installed
        ? Path.Combine(AppContext.BaseDirectory, "firmware", "glasslink_du.bin")
        : Config.Read(root => (root["firmware"] as JsonObject)?["image"].Text())
          ?? new[] { Path.Combine(Root, "firmware", "build", "glasslink_du.bin"), Path.Combine(AppContext.BaseDirectory, "firmware", "glasslink_du.bin") }
              .FirstOrDefault(File.Exists) ?? Path.Combine(Root, "firmware", "build", "glasslink_du.bin");

    /// <summary>Installed by the setup program (its uninstaller is next to GlassLink.exe), not run from a checkout.</summary>
    public static bool Installed => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public void Start()
    {
        Log($"GlassLink DMC {Version} ({Build}), configuration {ConfigPath}");
        Sim.Start();
        XPlane.Start();
        Displays.StartAll();
        Dus.Start();
        Updater.Start();
        // always made: "popout.auto" is a switch that takes effect at once (tray menu, Setup tab), and "Pop out missing
        // displays now" works while it is off
        Auto = new AutoPopout(Config, Camera, Displays.MissingSimDisplays, Log);
        XPlaneAuto = new XPlanePopout(XPlane, Displays.MissingDisplays, Log, enabled: () => AutoPopoutOn,
            profiles: path => XPlaneProfiles.Select(Config.Snapshot(), path));       // built-in and config.json's
    }

    /// <summary>Pop out missing displays by itself ("popout.auto").</summary>
    public bool AutoPopoutOn => Config.Read(root => PopoutSettings.From(root).Auto);

    /// <summary>Switches the automatic pop-out on or off; a countdown that runs is called off.</summary>
    public void SetAutoPopout(bool on)
    {
        Config.Update(root => ConfigFile.Section(root, "popout")["auto"] = on);
        Log($"automatic pop-out {(on ? "on" : "off")}");
    }

    public void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        Console.WriteLine(line);
        try
        {
            lock (_logTail)                                  // also to a file: a tray program has no console
            {
                Directory.CreateDirectory(Path.Combine(Root, "logs"));
                File.AppendAllText(Path.Combine(Root, "logs", $"dmc-{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        lock (_logTail)
        {
            _logTail.Enqueue(line);
            while (_logTail.Count > 200)
            {
                _logTail.Dequeue();
            }
        }
    }

    public IReadOnlyList<string> LogTail()
    {
        lock (_logTail)
        {
            return [.. _logTail];
        }
    }

    /// <summary>Carries out a command from the status page. Returns false if the DU is not connected.</summary>
    public bool Command(string serial, string command, int arg)
    {
        if (Dus.Connection(serial) is not { Alive: true } du)
        {
            return false;
        }

        switch (command)
        {
            case "ident":
                du.Ident(Dus.Settings(serial).Label, arg);
                break;
            case "ping":
                du.Ping();
                break;
            case "update":
                if (du.Ota.State == OtaState.Running)
                {
                    throw new DisplayException("an update is already running on this DU");
                }

                try
                {
                    var image = Firmware.Load(FirmwareImagePath);
                    if (du.Info?.SignedUpdates == true && !image.Signed)
                    {
                        // the DU would refuse it at the end of the transfer: said before, and how to go on instead
                        throw new DisplayException("this DU runs released firmware, which installs only images signed for GlassLink releases; "
                            + "this one is not signed (a development build): flash it over the DU's USB-C port (firmware/README.md)");
                    }

                    du.BeginUpdate(image.Data);
                }
                catch (InvalidDataException ex)
                {
                    throw new DisplayException(ex.Message);
                }

                break;
            case "reboot":
                du.Reboot();
                break;
            case "info":
                du.Send(GlassLink.Core.Protocol.MessageType.GetInfo);
                break;
            default:
                throw new DisplayException($"unknown command '{command}'");
        }

        return true;
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Log("stopping");
        Learner.CancelAndWait();                             // a Learn in progress brings the user's view back first (#39)
        Auto?.Dispose();                                     // and a running pop-out stops after its current display
        XPlaneAuto?.Dispose();
        Dus.Dispose();                                       // the panels fall back to NOT ASSIGNED
        Displays.Dispose();                                  // capture sessions are closed one by one, never killed
        Sim.Dispose();
        XPlane.Dispose();
        Updater.Dispose();
    }

    /// <summary>Runs <paramref name="work"/> on another thread and waits for it at most <paramref name="limit"/>: for the
    /// crash path, where the crashing thread may hold a lock the work needs. False if it failed or did not finish.</summary>
    public static bool Within(Action work, TimeSpan limit)
    {
        try
        {
            return Task.Run(work).Wait(limit);
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private string? ReadText(string name)
    {
        // an installed copy: only its install folder (VERSION decides what the DUs are offered, see FirmwareImagePath)
        foreach (var folder in Installed ? [AppContext.BaseDirectory] : new[] { Root, AppContext.BaseDirectory })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                return File.ReadAllText(path).Trim();
            }
        }

        return null;
    }

    private static string GitDescribe(string folder)
    {
        try
        {
            using var git = System.Diagnostics.Process.Start(new ProcessStartInfo("git", "describe --always --dirty --abbrev=7 --exclude *")
            {
                WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            var text = git?.StandardOutput.ReadToEnd().Trim() ?? "";
            git?.WaitForExit(3000);
            return text;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>The web server's port from the "server" section: 8765 unless it holds a usable port number (a hand-edited
    /// "8766" in quotes or a 0 must not stop the DMC from starting).</summary>
    public static int PortFrom(JsonNode? server) =>
        server?["port"].Number(8765) is var p && p is >= 1 and <= 65535 ? (int)p : 8765;

    /// <summary>Keep out of the simulator's way: below-normal priority, and only the last third of the logical CPUs on
    /// machines with eight or more ("process" section of the configuration: priority, affinity "auto" | [cpus] | null).</summary>
    private (string, int[]) TuneProcess()
    {
        var section = Config.Read(root => root["process"]?.DeepClone() as JsonObject);
        var priority = section?["priority"].Text() ?? "below_normal";
        var me = System.Diagnostics.Process.GetCurrentProcess();
        me.PriorityClass = priority switch
        {
            "normal" => ProcessPriorityClass.Normal,
            "idle" => ProcessPriorityClass.Idle,
            _ => ProcessPriorityClass.BelowNormal,
        };

        var count = Environment.ProcessorCount;
        int[] cpus = section?["affinity"] switch
        {
            JsonArray list => list.Select(n => (int)n.Number(-1)).Where(n => n >= 0 && n < count).ToArray(),
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String && v.GetValue<string>() == "auto" && count >= 8
                => Enumerable.Range(count - count / 3, count / 3).ToArray(),
            null when count >= 8 => Enumerable.Range(count - count / 3, count / 3).ToArray(),
            _ => [],
        };
        if (cpus.Length > 0)
        {
            me.ProcessorAffinity = (nint)cpus.Aggregate(0L, (mask, cpu) => mask | (1L << cpu));
        }

        return (priority, cpus);
    }
}
