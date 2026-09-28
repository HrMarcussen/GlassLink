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
    /// <summary>The mode each DU was last asked for, and on which connection: the DU restarts into it, so the same
    /// connection still reporting the old mode is only "not restarted yet", a new one is a refusal.</summary>
    private readonly Dictionary<string, (DuConnection Conn, int Mode)> _modeSent = [];
    private readonly HashSet<string> _cards = [];
    private readonly Dictionary<string, List<(string Display, DuConnection.Tile Tile)>> _layouts = [];
    private readonly Dictionary<string, string> _layoutProblems = [];
    /// <summary>Per DU: the band that carries its tiles, and what it was made for (tiles and displays).</summary>
    private readonly Dictionary<string, (string Key, IFrameSource Band)> _bands = [];
    private Timer? _scanTimer;
    private Timer? _brightnessTimer;

    /// <summary>The cockpit's brightness for a display, 0..1 (the sim's knob); null = not known, the trim alone counts.
    /// Set by the brightness link (layer 3).</summary>
    public Func<string, double?> SimBrightness { get; set; } = _ => null;

    /// <summary>A display's picture size (its client_size), for the size of its tile; null = 768x768.</summary>
    public Func<string, (int Width, int Height)?> DisplaySize { get; set; } = _ => null;

    /// <summary>Makes the picture that carries several displays to a DU as one band (name, width, height, where each
    /// display goes); null = every tile is sent on its own. Set by the DMC (the encoder lives in the capture layer).</summary>
    public Func<string, int, int, IReadOnlyList<BandPart>, IFrameSource>? BandFactory { get; set; }

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

        var dead = new List<DuConnection>();
        var deadBands = new List<IFrameSource>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;                                      // a scan that was waiting for the gate while Dispose ran
            }

            foreach (var (serial, conn) in _connections.Where(kv => !kv.Value.Alive).ToList())
            {
                dead.Add(conn);                     // unplugged or failed: freed below, outside the gate (#28)
                _connections.Remove(serial);
                _brightnessSent.Remove(serial);
                _rotationSent.Remove(serial);
                _modeSent.Remove(serial);
                if (_bands.Remove(serial, out var band))
                {
                    deadBands.Add(band.Band);        // nobody to send it to: stop composing (a new one comes with the DU)
                }
            }

            foreach (var path in paths)
            {
                var serial = DevicePaths.Serial(path);
                if (_connections.ContainsKey(serial))
                {
                    continue;
                }

                try
                {
                    var conn = new DuConnection(_open(path), _log);
                    _connections[serial] = conn;
                    _openErrors.Remove(serial);
                    conn.InfoReceived += c => Resync(c.Serial);      // mode and layout at once, not at the next scan (#33)
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

        foreach (var conn in dead)
        {
            conn.Dispose();
        }

        foreach (var band in deadBands)
        {
            (band as IDisposable)?.Dispose();
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
        var problems = new List<string>();
        if (s.Screen is { } screen && conn.Mode is { } mode && mode != screen)
        {
            if (!conn.SupportsMode)
            {
                problems.Add("this DU's firmware cannot change its screen mode: update it");
            }
            else if (_modeSent.TryGetValue(serial, out var asked) && asked.Mode == screen)
            {
                if (ReferenceEquals(asked.Conn, conn))
                {
                    return;                                  // asked, and it has not restarted yet: nothing else to send meanwhile
                }

                problems.Add($"the DU stayed in mode {mode} instead of {screen} (its screen may not take that mode)");     // once: no loop
            }
            else
            {
                _modeSent[serial] = (conn, screen);
                _log?.Invoke($"DU {Short(serial)}: HDMI mode {screen} (has {mode}); it restarts");
                conn.SetMode(screen);
                return;
            }
        }

        if (conn.Mode is { } reached && s.Screen == reached)
        {
            _modeSent.Remove(serial);                        // there: a later change of mind may ask again
        }

        // Tiles that do not fit the screen the DU reports are left out (the DU would ignore them and the frames
        // would be wasted); the status page says so. The size is the picture the display really publishes, rounded
        // up to whole 16-pixel blocks (the DU's decoder works in 16 x 16 blocks) (#27).
        var layout = new List<(string Display, DuConnection.Tile Tile)>();
        foreach (var (display, x, y) in s.Tiles)
        {
            if (_display(display) is null)
            {
                problems.Add($"{display} is not a display (any more)");
                continue;
            }

            if (layout.Count == MaxTiles)
            {
                problems.Add($"{display}: a DU shows at most {MaxTiles} displays");
                continue;
            }

            var (w, h) = DisplaySize(display) ?? (768, 768);
            var tile = new DuConnection.Tile(Math.Max(0, x), Math.Max(0, y), Math.Max(16, Align16(w)), Math.Max(16, Align16(h)));
            if (tile.X + tile.Width > conn.Info.PanelWidth || tile.Y + tile.Height > conn.Info.PanelHeight)
            {
                problems.Add($"{display} {tile.Width}x{tile.Height} at {tile.X},{tile.Y} lies outside the {conn.Info.PanelWidth}x{conn.Info.PanelHeight} screen");
                continue;
            }

            layout.Add((display, tile));
        }

        if (s.Tiles.Count > 0 && !conn.SupportsTiles)
        {
            problems.Add("this DU's firmware cannot show several displays: update it");
        }

        var problem = string.Join("; ", problems);
        if (_layoutProblems.GetValueOrDefault(serial, "") != problem)
        {
            _layoutProblems[serial] = problem;
            if (problem.Length > 0)
            {
                _log?.Invoke($"DU {Short(serial)}: {problem}");
            }
        }

        if (s.Tiles.Count > 0 && conn.SupportsTiles)
        {
            // In tile mode even when every tile had to be left out: the single display is not the user's choice then.
            var cards = _cards.Contains(serial);
            List<DuConnection.Tile> wire = [.. layout.Select(l => l.Tile)];
            var sources = layout.Select(l => _display(l.Display)).ToArray();
            // Firmware that takes a band gets all tiles as one picture as wide as its screen: decoded straight into
            // the frame buffer instead of one decode and copy per tile. Test cards stay per tile (the DU draws them).
            if (!cards && layout.Count > 0 && conn.SupportsBand && BandFactory is { } factory
                && Band(conn.Info.PanelWidth, conn.Info.PanelHeight, wire) is { } band)
            {
                var key = $"{band}|{string.Join(";", layout.Select(l => $"{l.Display}@{l.Tile}"))}";
                if (!_bands.TryGetValue(serial, out var made) || made.Key != key)
                {
                    DropBand(serial);
                    var parts = layout.Select(l => new BandPart(_display(l.Display)!, l.Tile.X, l.Tile.Y - band.Y, l.Tile.Width, l.Tile.Height)).ToList();
                    made = (key, factory($"band {Short(serial)}", band.Width, band.Height, parts));
                    _bands[serial] = made;
                }

                wire = [band];
                sources = [made.Band];
            }
            else
            {
                DropBand(serial);
            }

            if (!conn.Layout.SequenceEqual(wire) || conn.Cards != cards)
            {
                conn.SetLayout(wire, cards);                 // replaces the single display: no NOT ASSIGNED flash
                _log?.Invoke($"DU {Short(serial)}: layout {string.Join(", ", layout.Select(l => $"{l.Display} {l.Tile.Width}x{l.Tile.Height} at {l.Tile.X},{l.Tile.Y}"))}" +
                             $"{(cards ? " (test cards)" : wire.Count == 1 && _bands.ContainsKey(serial) ? $" (as one band {wire[0].Width}x{wire[0].Height} at y {wire[0].Y})" : "")}");
            }

            if (!conn.TileSources.SequenceEqual(sources))
            {
                conn.TileSources = sources;
            }

            _layouts[serial] = layout;
        }
        else if (conn.Layout.Count > 0)
        {
            conn.SetLayout([]);                              // back to one display
            conn.Source = _display(s.Display);
            _layouts.Remove(serial);
            DropBand(serial);
        }
    }

    /// <summary>The band for these tiles on a screen of this size: full width, from the first row a tile uses to the
    /// last, in whole 16-row blocks (the decoder writes whole blocks: a band of 1080 rows would decode as 1088 and
    /// not fit), starting on an even row (frame buffer rows of 800 x 3 bytes reach the cache line only every other
    /// row); null when that does not fit.</summary>
    public static DuConnection.Tile? Band(int panelWidth, int panelHeight, IReadOnlyList<DuConnection.Tile> tiles)
    {
        if (tiles.Count == 0 || panelWidth % 16 != 0)
        {
            return null;
        }

        var top = tiles.Min(t => t.Y) & ~1;
        var bottom = tiles.Max(t => t.Y + t.Height);
        var height = Align16(bottom - top);
        if (height > panelHeight)
        {
            return null;
        }

        if (top + height > panelHeight)
        {
            top = (panelHeight - height) & ~1;               // up, still covering every row (panel heights are even)
        }

        return new DuConnection.Tile(0, top, panelWidth, height);
    }

    private void DropBand(string serial)
    {
        if (_bands.Remove(serial, out var made))
        {
            (made.Band as IDisposable)?.Dispose();
        }
    }

    /// <summary>The most displays a DU shows at once (the firmware's tile slots).</summary>
    public const int MaxTiles = 6;

    /// <summary>Up to whole 16-pixel blocks: the DU's JPEG decoder writes 4:2:0 pictures in 16 x 16 blocks and
    /// refuses a buffer that does not hold them (#15, #27).</summary>
    public static int Align16(int v) => Math.Max(0, (v + 15) / 16 * 16);

    private static string Short(string serial) => serial[..Math.Min(8, serial.Length)];

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
        if (tiles is { Count: > MaxTiles })
        {
            throw new ArgumentException($"a DU shows at most {MaxTiles} displays");
        }

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
        lock (_gate)
        {
            if (on)
            {
                _cards.Add(serial);
            }
            else
            {
                _cards.Remove(serial);
            }
        }

        Resync(serial);
    }

    private void Resync(string serial)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(serial, out var conn))
            {
                SyncScreen(serial, conn);
            }
        }
    }

    // -- brightness: cockpit knob x the DU's trim ------------------------------------------------------
    public void BrightnessTick()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return;                                          // a scan or a slow DU holds the gate: the next tick (100 ms) comes soon (#28)
        }

        try
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
        finally
        {
            Monitor.Exit(_gate);
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
            .Where(kv => (kv.Value as JsonObject)?["display"].Text() == display).Select(kv => kv.Key).ToList() ?? []);
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
            foreach (var serial in _bands.Keys.ToList())
            {
                DropBand(serial);
            }
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
