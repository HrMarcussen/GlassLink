using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlassLink.Core.Config;

namespace GlassLink.Sim;

/// <summary>
/// X-Plane 12's own web API (12.1.1 and later, on by default, localhost:8086): the datarefs and commands of X-Plane
/// and of the aircraft's plugins, read and triggered over HTTP. Nothing is installed in X-Plane. Ids hold for one
/// X-Plane session only, so they are looked up by name and forgotten whenever X-Plane does not answer.
/// </summary>
public sealed class XPlaneClient : IDisposable
{
    public const string Process = "X-Plane.exe";

    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, long> _ids = [];
    private readonly Timer _timer;
    private volatile bool _connected;
    private int _failures, _pathMisses;
    private string _aircraftPath = "", _aircraftName = "";
    private double? _fps;

    public XPlaneClient(Action<string>? log = null, int port = 8086)
    {
        _log = log;
        // 127.0.0.1, not localhost: X-Plane listens on IPv4 only, and "localhost" would try IPv6 first on every request
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/v3/"), Timeout = TimeSpan.FromSeconds(2) };
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>X-Plane runs and its web API answers.</summary>
    public bool Connected => _connected;

    /// <summary>The loaded aircraft's .acf file, relative to the X-Plane folder ("Aircraft/ToLissA321_V1p7p2/a321.acf"); "" without one.</summary>
    public string AircraftPath => _aircraftPath;

    /// <summary>The aircraft's name as X-Plane shows it ("ToLiSs A321 Hi Def").</summary>
    public string AircraftName => _aircraftName;

    public double? Fps => _fps;

    public void Start() => _timer.Change(0, 2000);

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }

    /// <summary>A dataref's value as JSON (a number, an array, or base64 text for "data" datarefs); null if unknown or X-Plane is away.</summary>
    public JsonNode? Value(string dataref)
    {
        if (Id("datarefs", dataref) is not { } id)
        {
            return null;
        }

        var answer = Get($"datarefs/{id}/value");
        if (answer is null)
        {
            Forget(dataref);                                 // a stale id (another session) is looked up again next time
        }

        return answer?["data"];
    }

    public double? Number(string dataref) => Value(dataref) is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.AsDouble() : null;

    public double[]? Numbers(string dataref) =>
        Value(dataref) is JsonArray a ? [.. a.Select(n => n is not null && n.GetValueKind() == JsonValueKind.Number ? n.AsDouble() : double.NaN)] : null;

    /// <summary>A "data" dataref as text (X-Plane sends it base64-encoded, padded with zero bytes).</summary>
    public string? Text(string dataref)
    {
        if (Value(dataref) is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(v.GetValue<string>())).TrimEnd('\0').Trim();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Triggers a command once, as a button press would. False if X-Plane does not know it or is away.</summary>
    public bool Command(string name)
    {
        if (Id("commands", name) is not { } id)
        {
            return false;
        }

        try
        {
            using var body = new StringContent("{\"duration\":0}", Encoding.UTF8, "application/json");
            using var response = _http.PostAsync($"command/{id}/activate", body).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                Forget(name);
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Whether X-Plane knows a command (an aircraft plugin registers its own when it loads).</summary>
    public bool Knows(string command) => Id("commands", command) is not null;

    private long? Id(string kind, string name)
    {
        lock (_ids)
        {
            if (_ids.TryGetValue(kind + ":" + name, out var known))
            {
                return known;
            }
        }

        if (Get($"{kind}?filter[name]={name}")?["data"] is JsonArray { Count: > 0 } list && list[0]?["id"] is { } idNode)
        {
            var id = (long)idNode.AsDouble();
            lock (_ids)
            {
                _ids[kind + ":" + name] = id;
            }

            return id;
        }

        return null;
    }

    private void Forget(string name)
    {
        lock (_ids)
        {
            _ids.Remove("datarefs:" + name);
            _ids.Remove("commands:" + name);
        }
    }

    private JsonNode? Get(string path)
    {
        try
        {
            using var response = _http.GetAsync(path).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode ? JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Every 2 s: is X-Plane there, which aircraft, how fast does it run.</summary>
    private void Poll()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Process));
            var running = processes.Length > 0;
            Array.ForEach(processes, p => p.Dispose());
            // /api/capabilities answers from X-Plane's start on, home screen included; the datarefs only with a flight
            var answer = running ? Get("../capabilities") : null;
            _failures = answer is null ? _failures + 1 : 0;
            if (answer is null && (!running || _failures >= 3 || !_connected))    // a few slow answers while a flight loads are no reason
            {
                if (_connected)
                {
                    _log?.Invoke(running ? "X-Plane: the web API stopped answering" : "X-Plane: closed");
                }

                (_connected, _aircraftPath, _aircraftName, _fps) = (false, "", "", null);
                lock (_ids)
                {
                    _ids.Clear();
                }

                return;
            }

            if (answer is null)
            {
                return;
            }

            if (!_connected)
            {
                _connected = true;
                _log?.Invoke($"X-Plane {answer["x-plane"]?["version"]?.ToString() ?? ""}: connected to its web API");
            }

            var read = Text("sim/aircraft/view/acf_relative_path");
            _pathMisses = read is null ? _pathMisses + 1 : 0;
            var path = read ?? (_pathMisses >= 3 ? "" : _aircraftPath);   // no flight (home screen), or just one slow answer
            if (path != _aircraftPath)
            {
                _aircraftPath = path;
                _aircraftName = Text("sim/aircraft/view/acf_ui_name") ?? Path.GetFileNameWithoutExtension(path);
                _log?.Invoke($"X-Plane: aircraft '{_aircraftName}' ({(path.Length > 0 ? path : "none")})");
            }

            _fps = Number("sim/time/framerate_period") is > 0 and var period ? Math.Round(1 / period, 1) : null;
        }
        catch (Exception ex)                                 // a timer callback must never take the process down
        {
            _log?.Invoke($"X-Plane: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"X-Plane connected={_connected} aircraft='{_aircraftName}' fps={_fps}");
}
