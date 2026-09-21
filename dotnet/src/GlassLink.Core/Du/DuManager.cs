using System.Text.Json.Nodes;
using GlassLink.Core.Config;
using GlassLink.Core.Usb;

namespace GlassLink.Core.Du;

/// <summary>One row of the DU list: what the configuration says plus what the DU reports.</summary>
public sealed record DuStatus(string Serial, string Label, string Display, int Trim, bool Alive, string Error, DuInfo? Info, DuStats? Stats,
    long FramesSent, double? PingMs, int? BrightnessSent, double? BrightnessSim, OtaStatus Ota, IReadOnlyList<string> Health);

/// <summary>
/// Finds DUs as they are plugged in, gives each its display from the configuration, and carries the user's commands.
/// A DU is known by its serial number; which display it shows is the DMC's decision, not the DU's (any DU can take
/// any position in the cockpit). Port of the Python ModuleManager.
/// </summary>
public sealed class DuManager : IDisposable
{
    private readonly ConfigFile _config;
    private readonly Func<string, IFrameSource?> _display;
    private readonly Func<IReadOnlyList<string>> _findPaths;
    private readonly Func<string, IDuTransport> _open;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, DuConnection> _connections = [];
    private readonly Dictionary<string, int> _brightnessSent = [];
    private readonly Dictionary<string, double?> _brightnessSim = [];
    private readonly Dictionary<string, string> _openErrors = [];
    private Timer? _scanTimer;
    private Timer? _brightnessTimer;

    /// <summary>The cockpit's brightness for a display, 0..1 (the sim's knob); null = not known, the trim alone counts.
    /// Set by the brightness link (layer 3).</summary>
    public Func<string, double?> SimBrightness { get; set; } = _ => null;

    public string LastScanError { get; private set; } = "";

    public DuManager(ConfigFile config, Func<string, IFrameSource?> display, Func<IReadOnlyList<string>> findPaths,
        Func<string, IDuTransport> open, Action<string>? log = null)
    {
        (_config, _display, _findPaths, _open, _log) = (config, display, findPaths, open, log);
    }

    /// <summary>A manager for real hardware.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static DuManager ForWinUsb(ConfigFile config, Func<string, IFrameSource?> display, Action<string>? log = null) =>
        new(config, display, WinUsbTransport.FindDevicePaths, path => new WinUsbTransport(path), log);

    public void Start(int scanMs = 2000, int brightnessMs = 100)
    {
        _scanTimer = new Timer(_ => Guarded(ScanOnce), null, 0, scanMs);
        _brightnessTimer = new Timer(_ => Guarded(BrightnessTick), null, brightnessMs, brightnessMs);
    }

    // -- hot-plug -----------------------------------------------------------------------------------
    public void ScanOnce()
    {
        IReadOnlyList<string> paths;
        try
        {
            paths = _findPaths();
            LastScanError = "";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            LastScanError = ex.Message;
            return;
        }

        lock (_gate)
        {
            foreach (var (serial, conn) in _connections.Where(kv => !kv.Value.Alive).ToList())
            {
                conn.Dispose();                     // unplugged or failed: free the handle so it can come back
                _connections.Remove(serial);
                _brightnessSent.Remove(serial);
            }

            foreach (var path in paths)
            {
                var serial = WinUsbTransport.SerialFromPath(path);
                if (_connections.ContainsKey(serial))
                {
                    continue;
                }

                try
                {
                    var conn = new DuConnection(_open(path), _log);
                    _connections[serial] = conn;
                    _openErrors.Remove(serial);
                    conn.Start();
                    conn.Source = _display(Settings(serial).Display);
                }
                catch (IOException ex)
                {
                    if (!_openErrors.TryGetValue(serial, out var last) || last != ex.Message)
                    {
                        _log?.Invoke(ex.Message);
                    }

                    _openErrors[serial] = ex.Message;
                }
            }
        }
    }

    // -- brightness: cockpit knob x the DU's trim ------------------------------------------------------
    public void BrightnessTick()
    {
        lock (_gate)
        {
            foreach (var (serial, conn) in _connections)
            {
                if (!conn.Alive || conn.Info is null)
                {
                    continue;
                }

                var settings = Settings(serial);
                var sim = settings.Display.Length > 0 ? SimBrightness(settings.Display) : null;
                var percent = sim is { } s ? (int)Math.Round(Math.Clamp(s, 0, 1) * settings.Brightness) : settings.Brightness;
                _brightnessSim[serial] = sim;
                if (!_brightnessSent.TryGetValue(serial, out var sent) || sent != percent)
                {
                    conn.SetBrightness(percent);
                    _brightnessSent[serial] = percent;
                }
            }
        }
    }

    // -- the user's decisions ---------------------------------------------------------------------------
    public DuSettings Settings(string serial) =>
        _config.Read(root => DuSettings.From((root["modules"] as JsonObject)?[serial] as JsonObject));

    /// <summary>Changes what is stored about a DU; null leaves a value as it is. Applies at once if the DU is connected.</summary>
    public DuSettings Assign(string serial, string? display = null, string? label = null, int? brightness = null)
    {
        _config.Update(root =>
        {
            var entry = ConfigFile.Section(ConfigFile.Section(root, "modules"), serial);
            if (display is not null)
            {
                entry["display"] = display;
            }

            if (label is not null)
            {
                entry["label"] = label.Trim();
            }

            if (brightness is { } b)
            {
                entry["brightness"] = Math.Clamp(b, 0, 100);
            }
        });
        var settings = Settings(serial);
        lock (_gate)
        {
            if (display is not null && _connections.TryGetValue(serial, out var conn))
            {
                conn.Source = _display(settings.Display);
            }
        }

        return settings;
    }

    /// <summary>A display was removed in the editor: DUs that showed it fall back to NOT ASSIGNED.</summary>
    public void DisplayRemoved(string display)
    {
        var serials = _config.Read(root => (root["modules"] as JsonObject)?
            .Where(kv => (kv.Value as JsonObject)?["display"]?.GetValue<string>() == display).Select(kv => kv.Key).ToList() ?? []);
        foreach (var serial in serials)
        {
            Assign(serial, display: "");
        }
    }

    /// <summary>Forgets a DU that is not connected. A connected one would only come back at the next scan.</summary>
    public bool Forget(string serial)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(serial, out var conn) && conn.Alive)
            {
                return false;
            }
        }

        _config.Update(root => (root["modules"] as JsonObject)?.Remove(serial));
        return true;
    }

    public DuConnection? Connection(string serial)
    {
        lock (_gate)
        {
            return _connections.GetValueOrDefault(serial);
        }
    }

    /// <summary>Connected DUs, plus configured ones that are not plugged in (so a missing DU is visible).</summary>
    public IReadOnlyList<DuStatus> Status()
    {
        var configured = _config.Read(root => (root["modules"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? []);
        lock (_gate)
        {
            return _connections.Keys.Union(configured).Order().Select(serial =>
            {
                var s = Settings(serial);
                var c = _connections.GetValueOrDefault(serial);
                return new DuStatus(serial, s.Label, s.Display, s.Brightness, c?.Alive ?? false, c?.Error ?? _openErrors.GetValueOrDefault(serial, ""),
                    c?.Info, c?.Stats, c?.FramesSent ?? 0, c?.PingMs, _brightnessSent.TryGetValue(serial, out var b) ? b : null,
                    _brightnessSim.GetValueOrDefault(serial), c?.Ota ?? new OtaStatus(OtaState.Idle, 0, ""), c?.HealthReasons ?? []);
            }).ToList();
        }
    }

    public void Dispose()
    {
        _scanTimer?.Dispose();
        _brightnessTimer?.Dispose();
        lock (_gate)
        {
            foreach (var conn in _connections.Values)
            {
                conn.Source = null;                 // the panels show NOT ASSIGNED rather than a frozen last picture
                conn.Dispose();
            }

            _connections.Clear();
        }
    }

    private void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)                        // a timer callback must never take the process down
        {
            _log?.Invoke($"DU manager: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
