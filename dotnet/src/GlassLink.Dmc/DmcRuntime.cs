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
        Displays = new DisplayRegistry(Config, name => Dus?.IsShown(name) == true, Log)
        {
            Alternative = name => XPlaneProfile?.Rule(name),     // the same display names find the X-Plane aircraft's pop-outs
        };
        Dus = DuManager.ForWinUsb(Config, Displays.Slot, Log);
        Dus.BandFactory = (name, width, height, parts) => new BandComposer(name, width, height, parts);
        // X-Plane's aircraft dim their pop-outs themselves (the ToLiss does, measured 2 Oct 2026): nothing to add on the DU
        Dus.SimBrightness = display => BrightnessEnabled && !XPlane.Connected ? Brightness.For(display) : null;
        // The size of the picture a display really publishes (after max_size); its configured client_size before the
        // first frame (#27).
        Dus.DisplaySize = display => Displays.Slot(display) is { Width: > 0, Height: > 0 } slot ? (slot.Width, slot.Height)
            : Config.Read<(int, int)?>(root => (root["displays"] as JsonObject)?[display] is JsonObject d && d["client_size"] is JsonArray a && a.Count == 2
                ? ((int)a[0]!.AsDouble(), (int)a[1]!.AsDouble()) : null);
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

    /// <summary>The profile of the aircraft loaded in X-Plane; null without X-Plane, an aircraft or a profile for it.</summary>
    public XPlaneProfile? XPlaneProfile => XPlane.Connected ? XPlaneProfiles.Select(XPlane.AircraftPath) : null;

    /// <summary>The automatic pop-out of the sim that runs: X-Plane's while X-Plane answers, else the MSFS one.</summary>
    public AutoPopoutState? PopoutState => XPlane.Connected ? XPlaneAuto?.State : Auto?.State;

    /// <summary>Closes a display's pop-out so it is popped out afresh. X-Plane's are closed with the aircraft's own
    /// command: X-Plane would take a close message to the window as "quit X-Plane". False if it could not be closed.</summary>
    public bool ClosePopout(string name, WindowInfo window)
    {
        if (string.Equals(window.Process, XPlaneClient.Process, StringComparison.OrdinalIgnoreCase))
        {
            if (XPlaneProfile?.Displays.GetValueOrDefault(name) is not { } display || !XPlane.Command(display.Command))
            {
                return false;
            }
        }
        else if (!WindowFinder.Close(window.Handle))
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

    /// <summary>The DU firmware image to install: named in the configuration, else the one built in this checkout,
    /// else the one shipped next to GlassLink.exe (an installed copy).</summary>
    public string FirmwareImagePath => Config.Read(root => (root["firmware"] as JsonObject)?["image"].Text())
                                       ?? new[] { Path.Combine(Root, "firmware", "build", "glasslink_du.bin"), Path.Combine(AppContext.BaseDirectory, "firmware", "glasslink_du.bin") }
                                           .FirstOrDefault(File.Exists) ?? Path.Combine(Root, "firmware", "build", "glasslink_du.bin");

    public void Start()
    {
        Log($"GlassLink DMC {Version} ({Build}), configuration {ConfigPath}");
        Sim.Start();
        XPlane.Start();
        Displays.StartAll();
        Dus.Start();
        Updater.Start();
        if (PopoutSettings.From(Config.Root).Auto)
        {
            Auto = new AutoPopout(Config, Camera, Displays.MissingSimDisplays, Log);
            XPlaneAuto = new XPlanePopout(XPlane, Displays.MissingDisplays, Log);
        }
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
                    du.BeginUpdate(Firmware.Load(FirmwareImagePath).Data);
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

    private string? ReadText(string name)
    {
        foreach (var folder in new[] { Root, AppContext.BaseDirectory })
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
            JsonArray list => list.Select(n => (int)n!.AsDouble()).Where(n => n >= 0 && n < count).ToArray(),
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
