using System.Text.Json.Nodes;
using GlassLink.Core.Config;
using GlassLink.Core.Usb;

namespace GlassLink.Core.Du;

/// <summary>One row of the DU list: what the configuration says plus what the DU reports.</summary>
public sealed record DuStatus(string Serial, string Label, string Display, int Trim, bool Alive, string Error, DuInfo? Info, DuStats? Stats,
    long FramesSent, double? PingMs, int? BrightnessSent, double? BrightnessSim, OtaStatus Ota, IReadOnlyList<string> Health,
    int? Screen, IReadOnlyList<(string Display, int X, int Y)> Tiles, IReadOnlyList<(string Display, DuConnection.Tile Tile)> Layout, bool Cards, string LayoutProblem);

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
    private volatile HashSet<string> _lastShown = [];
    private bool _disposed;
    private readonly Dictionary<string, DuConnection> _connections = [];
    private readonly Dictionary<string, int> _brightnessSent = [];
    private readonly Dictionary<string, double?> _brightnessSim = [];
    private readonly Dictionary<string, string> _openErrors = [];
    private readonly HashSet<string> _rotationSent = [];
    private readonly HashSet<string> _modeSent = [];
    private readonly HashSet<string> _cards = [];
    private readonly Dictionary<string, List<(string Display, DuConnection.Tile Tile)>> _layouts = [];
    private readonly Dictionary<string, string> _layoutProblems = [];
    private Timer? _scanTimer;
    private Timer? _brightnessTimer;

    /// <summary>The cockpit's brightness for a display, 0..1 (the sim's knob); null = not known, the trim alone counts.
    /// Set by the brightness link (layer 3).</summary>
    public Func<string, double?> SimBrightness { get; set; } = _ => null;

    /// <summary>A display's picture size (its client_size), for the size of its tile; null = 768x768.</summary>
    public Func<string, (int Width, int Height)?> DisplaySize { get; set; } = _ => null;

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
            if (_disposed)
            {
                return;                                      // a scan that was waiting for the gate while Dispose ran
            }

            foreach (var (serial, conn) in _connections.Where(kv => !kv.Value.Alive).ToList())
            {
                conn.Dispose();                     // unplugged or failed: free the handle so it can come back
                _connections.Remove(serial);
                _brightnessSent.Remove(serial);
                _rotationSent.Remove(serial);
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
                    if (Settings(serial).Tiles.Count == 0)
                    {
                        conn.Source = _display(Settings(serial).Display);
                    }
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

            foreach (var (serial, conn) in _connections)
            {
                SyncScreen(serial, conn);
            }
        }
    }

    /// <summary>Brings a connected DU's HDMI mode and layout in line with its settings: the mode first (the DU restarts
    /// into it, so nothing else is sent until it is back), then the layout with the displays' sizes, then which
    /// display feeds which tile. Runs under the gate; sends only what changed.</summary>
    private void SyncScreen(string serial, DuConnection conn)
    {
        if (!conn.Alive || conn.Info is null)
        {
            return;
        }

        var s = Settings(serial);
        if (s.Screen is { } screen && conn.SupportsMode && conn.Mode is { } mode)
        {
            if (mode != screen)
            {
                if (_modeSent.Add(serial))                   // once per connection: a DU that refuses does not loop
                {
                    _log?.Invoke($"DU {serial[..8]}: HDMI mode {screen} (has {mode}); it restarts");
                    conn.SetMode(screen);
                }

                return;
            }

            _modeSent.Remove(serial);
        }

        // Tiles that do not fit the screen the DU reports are left out (the DU would ignore them and the frames
        // would be wasted); the status page says so.
        var layout = new List<(string Display, DuConnection.Tile Tile)>();
        var outside = new List<string>();
        foreach (var (display, x, y) in s.Tiles)
        {
            var (w, h) = DisplaySize(display) ?? (768, 768);
            var tile = new DuConnection.Tile(Math.Max(0, x), Math.Max(0, y), Math.Max(8, Snap(w)), Math.Max(8, Snap(h)));
            if (tile.X + tile.Width > conn.Info.PanelWidth || tile.Y + tile.Height > conn.Info.PanelHeight)
            {
                outside.Add($"{display} {tile.Width}x{tile.Height} at {tile.X},{tile.Y} lies outside the {conn.Info.PanelWidth}x{conn.Info.PanelHeight} screen");
                continue;
            }

            layout.Add((display, tile));
        }

        var problem = string.Join("; ", outside);
        if (_layoutProblems.GetValueOrDefault(serial, "") != problem)
        {
            _layoutProblems[serial] = problem;
            if (problem.Length > 0)
            {
                _log?.Invoke($"DU {serial[..8]}: {problem}");
            }
        }

        if (layout.Count > 0 && conn.SupportsTiles)
        {
            var cards = _cards.Contains(serial);
            if (!conn.Layout.SequenceEqual(layout.Select(l => l.Tile)) || conn.Cards != cards)
            {
                if (conn.Source is not null)
                {
                    conn.Source = null;
                }

                conn.SetLayout(layout.Select(l => l.Tile).ToList(), cards);
                _log?.Invoke($"DU {serial[..8]}: layout {string.Join(", ", layout.Select(l => $"{l.Display} {l.Tile.Width}x{l.Tile.Height} at {l.Tile.X},{l.Tile.Y}"))}{(cards ? " (test cards)" : "")}");
            }

            var sources = layout.Select(l => _display(l.Display)).ToArray();
            if (!conn.TileSources.SequenceEqual(sources))
            {
                conn.TileSources = sources;
            }

            _layouts[serial] = layout;
        }
        else if (conn.Layout.Count > 0)
        {
            conn.SetLayout([]);                              // back to one display
            conn.TileSources = new IFrameSource?[0];
            conn.Source = _display(s.Display);
            _layouts.Remove(serial);
        }
    }

    /// <summary>Picture sizes on multiples of 8: the DU's hardware JPEG decoder refuses others ("Picture sizes not divisible
    /// by 8 are not supported"); positions are free. The display editor rounds client_size the same way.</summary>
    private static int Snap(int v) => Math.Max(0, (v + 4) / 8 * 8);

    // -- screens and layouts (the user's decisions) ------------------------------------------------------------
    /// <summary>The HDMI mode a DU is asked for (null = leave it); applied at once if it is connected.</summary>
    public void SetScreen(string serial, int? screen)
    {
        _config.Update(root =>
        {
            var entry = ConfigFile.Section(ConfigFile.Section(root, "modules"), serial);
            if (screen is { } m)
            {
                entry["screen"] = Math.Clamp(m, 0, 4);
            }
            else
            {
                entry.Remove("screen");
            }
        });
        Resync(serial);
    }

    /// <summary>The DU's tiles: display -> position, in tile order; null or empty = a single-display DU again.</summary>
    public void SetTiles(string serial, IReadOnlyList<(string Display, int X, int Y)>? tiles)
    {
        _config.Update(root =>
        {
            var entry = ConfigFile.Section(ConfigFile.Section(root, "modules"), serial);
            if (tiles is { Count: > 0 })
            {
                var o = new JsonObject();
                foreach (var (display, x, y) in tiles)
                {
                    o[display] = new JsonObject { ["x"] = Math.Max(0, x), ["y"] = Math.Max(0, y) };
                }

                entry["tiles"] = o;
            }
            else
            {
                entry.Remove("tiles");
            }
        });
        Resync(serial);
    }

    /// <summary>Test cards on the DU's tiles while the layout is being lined up (not stored).</summary>
    public void ShowCards(string serial, bool on)
    {
        if (on)
        {
            _cards.Add(serial);
        }
        else
        {
            _cards.Remove(serial);
        }

        Resync(serial);
    }

    private void Resync(string serial)
    {
        lock (_gate)
        {
            _modeSent.Remove(serial);
            if (_connections.TryGetValue(serial, out var conn))
            {
                SyncScreen(serial, conn);
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
                if (settings.Rotation is { } rotation && _rotationSent.Add(serial))
                {
                    conn.Send(GlassLink.Core.Protocol.MessageType.SetRotation, arg: (uint)(((rotation % 360) + 360) % 360));   // once per connection
                }

                var knob = settings.Tiles.Count > 0 ? settings.Tiles[0].Display : settings.Display;     // a layout follows its first tile's knob
                var sim = knob.Length > 0 ? SimBrightness(knob) : null;
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
            if (display is not null && settings.Tiles.Count == 0 && _connections.TryGetValue(serial, out var conn))
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

        var tiled = _config.Read(root => (root["modules"] as JsonObject)?
            .Where(kv => ((kv.Value as JsonObject)?["tiles"] as JsonObject)?.ContainsKey(display) == true).Select(kv => kv.Key).ToList() ?? []);
        foreach (var serial in tiled)
        {
            SetTiles(serial, Settings(serial).Tiles.Where(t => t.Display != display).ToList());
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

    /// <summary>True while a connected DU shows this display: the capture layer runs a display at full rate only then.</summary>
    public bool IsShown(string display)
    {
        // Called for every captured frame of every display. A DU being disposed holds the gate for up to a few
        // seconds; rather than stall every capture meanwhile, the last answer stands until the gate is free again.
        if (!Monitor.TryEnter(_gate, 2))
        {
            return _lastShown.Contains(display);
        }

        try
        {
            var src = _display(display);
            var shown = src is not null && _connections.Any(kv => kv.Value.Alive && (ReferenceEquals(kv.Value.Source, src) || kv.Value.TileSources.Any(t => ReferenceEquals(t, src))));
            _lastShown = shown ? _lastShown.Contains(display) ? _lastShown : new HashSet<string>(_lastShown) { display }
                : _lastShown.Contains(display) ? new HashSet<string>(_lastShown.Where(d => d != display)) : _lastShown;
            return shown;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
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
                    _brightnessSim.GetValueOrDefault(serial), c?.Ota ?? new OtaStatus(OtaState.Idle, 0, ""), c?.HealthReasons ?? [],
                    s.Screen, s.Tiles, c?.Layout.Count > 0 ? _layouts.GetValueOrDefault(serial) ?? [] : [], c?.Cards ?? false, _layoutProblems.GetValueOrDefault(serial, ""));
            }).ToList();
        }
    }

    public void Dispose()
    {
        _scanTimer?.Dispose();
        _brightnessTimer?.Dispose();
        lock (_gate)
        {
            _disposed = true;
            foreach (var conn in _connections.Values)
            {
                conn.Source = null;                 // the panels show NOT ASSIGNED rather than a frozen last picture
                conn.Dispose();
            }

            _connections.Clear();
            _layouts.Clear();
            _layoutProblems.Clear();
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
