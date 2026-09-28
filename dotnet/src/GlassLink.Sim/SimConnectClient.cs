using System.Runtime.InteropServices;
using System.Text;

namespace GlassLink.Sim;

/// <summary>A value in the sim that is kept up to date: a simulation variable or an aircraft's local variable ("L:NAME").</summary>
public sealed class SimVariable
{
    internal SimVariable(uint id, string name, string unit, bool text)
    {
        (Id, Name, Unit, IsText) = (id, name, unit, text);
    }

    internal uint Id { get; }

    public string Name { get; }

    public string Unit { get; }

    public bool IsText { get; }

    /// <summary>The last value the sim sent; null until the first one arrives (and again after the sim went away).</summary>
    public double? Value { get; internal set; }

    public string? Text { get; internal set; }
}

/// <summary>
/// The native SimConnect client, straight on SimConnect.dll: no WASM module, no gateway, nothing to install in the
/// sim. Variables are registered once and the sim pushes every change; MSFS 2024 takes aircraft L:vars as ordinary
/// variable names ("L:N_DISPLAY_BRIGHTNESS_CO"). One thread owns the connection, waits for the sim to appear,
/// reconnects when it went away, and re-registers everything.
/// </summary>
public sealed class SimConnectClient : IDisposable
{
    private readonly string _appName;
    private readonly Action<string>? _log;
    private readonly List<SimVariable> _variables = [];
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private nint _handle;
    private int _registered;
    private volatile bool _connected;

    /// <summary>SimConnect.dll is looked for next to GlassLink.exe first (a checkout or a release that ships it), then in
    /// an installed MSFS SDK (its setup sets MSFS2024_SDK or MSFS_SDK), so a copy does not have to be distributed (#58).</summary>
    static SimConnectClient() => NativeLibrary.SetDllImportResolver(typeof(SimConnectClient).Assembly, (name, assembly, path) =>
    {
        if (!name.StartsWith("SimConnect", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        foreach (var candidate in SimConnectCandidates())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return 0;                                            // the default search, then DllNotFoundException: "SimConnect not available"
    });

    public static IEnumerable<string> SimConnectCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "SimConnect.dll");
        // where the DMC keeps its own files (config.json): a copy the user put there survives reinstalls and updates
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassLink", "SimConnect.dll");
        foreach (var variable in new[] { "MSFS2024_SDK", "MSFS_SDK" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } sdk)
            {
                yield return Path.Combine(sdk, "SimConnect SDK", "lib", "SimConnect.dll");
            }
        }
    }

    public SimConnectClient(string appName = "GlassLink DMC", Action<string>? log = null)
    {
        (_appName, _log) = (appName, log);
        _thread = new Thread(Run) { IsBackground = true, Name = "simconnect" };
    }

    public bool Connected => _connected;

    /// <summary>Frames the sim renders per second (its own count, before any frame generation).</summary>
    public double? SimFps { get; private set; }

    public void Start() => _thread.Start();

    /// <summary>Registers a numeric variable, e.g. ("COCKPIT CAMERA ZOOM", "Percent") or ("L:N_DISPLAY_BRIGHTNESS_CO", "Number").
    /// Asking for the same one again returns the same object.</summary>
    public SimVariable Number(string name, string unit = "Number") => Register(name, unit, text: false);

    public SimVariable Text(string name) => Register(name, "", text: true);

    /// <summary>Writes a settable variable. Returns false while the sim is not connected.</summary>
    public bool Set(SimVariable variable, double value)
    {
        lock (_gate)
        {
            if (!_connected || variable.IsText)
            {
                return false;
            }

            return Native.SimConnect_SetDataOnSimObject(_handle, variable.Id, 0, 0, 0, sizeof(double), ref value) == 0;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _signal.Set();
        if (_thread.IsAlive && !_thread.Join(2000))
        {
            return;                                          // still inside SimConnect_Open: the handles must stay valid for it
        }

        _signal.Dispose();
        _stop.Dispose();
    }

    private SimVariable Register(string name, string unit, bool text)
    {
        lock (_gate)
        {
            if (_variables.FirstOrDefault(v => v.Name == name && v.Unit == unit) is { } known)
            {
                return known;
            }

            var variable = new SimVariable((uint)_variables.Count + 1, name, unit, text);
            _variables.Add(variable);
            _signal.Set();                                   // the connection thread registers it with the sim
            return variable;
        }
    }

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (!_connected)
                {
                    if (Native.SimConnect_Open(out _handle, _appName, 0, 0, _signal.SafeWaitHandle.DangerousGetHandle(), 0) != 0)
                    {
                        _stop.Token.WaitHandle.WaitOne(3000);            // the sim is not running: look again in a while
                        continue;
                    }

                    lock (_gate)
                    {
                        _connected = true;
                        _registered = 0;
                        Native.SimConnect_SubscribeToSystemEvent(_handle, 1, "Frame");     // under the lock like every call (#43)
                    }

                    _log?.Invoke("SimConnect: connected");
                }

                RegisterPending();
                Pump();
                _signal.WaitOne(250);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)       // this thread must never take the DMC down (#43)
            {
                _log?.Invoke($"SimConnect: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                _stop.Token.WaitHandle.WaitOne(3000);
            }
        }

        Disconnect();
    }

    private void RegisterPending()
    {
        lock (_gate)
        {
            for (; _registered < _variables.Count; _registered++)
            {
                var v = _variables[_registered];
                Native.SimConnect_AddToDataDefinition(_handle, v.Id, v.Name, v.IsText ? null : v.Unit, v.IsText ? Native.STRING256 : Native.FLOAT64, 0, uint.MaxValue);
                Native.SimConnect_RequestDataOnSimObject(_handle, v.Id, v.Id, 0, v.IsText ? Native.PERIOD_SECOND : Native.PERIOD_SIM_FRAME, Native.FLAG_CHANGED, 0, 0, 0);
            }
        }
    }

    private unsafe void Pump()
    {
        while (NextDispatch(out var data, out var size))
        {
            var header = (uint*)data;
            switch (header[2])
            {
                case Native.RECV_SIMOBJECT_DATA when size >= 40:
                    var request = header[3];
                    SimVariable? v;
                    lock (_gate)
                    {
                        v = request >= 1 && request <= _variables.Count ? _variables[(int)request - 1] : null;
                    }

                    if (v is { IsText: true })
                    {
                        var bytes = new ReadOnlySpan<byte>((byte*)data + 40, (int)Math.Min(256, size - 40));
                        var end = bytes.IndexOf((byte)0);
                        v.Text = Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes);
                    }
                    else if (v is not null && size >= 48)
                    {
                        v.Value = *(double*)((byte*)data + 40);
                    }

                    break;
                case Native.RECV_EVENT_FRAME when size >= 28:
                    SimFps = *(float*)((byte*)data + 24);
                    break;
                case Native.RECV_QUIT:
                    _log?.Invoke("SimConnect: the sim closed the connection");
                    Disconnect();
                    return;
            }
        }
    }

    /// <summary>SimConnect is not thread-safe per handle: never inside the sim library at the same time as Set.</summary>
    private bool NextDispatch(out nint data, out uint size)
    {
        lock (_gate)
        {
            if (!_connected)
            {
                (data, size) = (0, 0);
                return false;
            }

            return Native.SimConnect_GetNextDispatch(_handle, out data, out size) == 0 && size >= 12;
        }
    }

    private void Disconnect()
    {
        lock (_gate)
        {
            if (_connected)
            {
                _connected = false;
                Native.SimConnect_Close(_handle);
                _handle = 0;
                SimFps = null;
                foreach (var v in _variables)
                {
                    (v.Value, v.Text) = (null, null);        // stale values must not look like live ones
                }
            }
        }
    }

    private static class Native
    {
        public const uint FLOAT64 = 4, STRING256 = 9, PERIOD_SIM_FRAME = 3, PERIOD_SECOND = 4, FLAG_CHANGED = 1;
        public const uint RECV_QUIT = 3, RECV_EVENT_FRAME = 7, RECV_SIMOBJECT_DATA = 8;

        [DllImport("SimConnect.dll", CharSet = CharSet.Ansi)]
        public static extern int SimConnect_Open(out nint handle, string name, nint hwnd, uint userEvent, nint eventHandle, uint configIndex);

        [DllImport("SimConnect.dll")]
        public static extern int SimConnect_Close(nint handle);

        [DllImport("SimConnect.dll", CharSet = CharSet.Ansi)]
        public static extern int SimConnect_AddToDataDefinition(nint handle, uint defineId, string datumName, string? unitsName, uint datumType, float epsilon, uint datumId);

        [DllImport("SimConnect.dll")]
        public static extern int SimConnect_RequestDataOnSimObject(nint handle, uint requestId, uint defineId, uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);

        [DllImport("SimConnect.dll")]
        public static extern int SimConnect_SetDataOnSimObject(nint handle, uint defineId, uint objectId, uint flags, uint arrayCount, uint unitSize, ref double data);

        [DllImport("SimConnect.dll")]
        public static extern int SimConnect_GetNextDispatch(nint handle, out nint data, out uint size);

        [DllImport("SimConnect.dll", CharSet = CharSet.Ansi)]
        public static extern int SimConnect_SubscribeToSystemEvent(nint handle, uint eventId, string eventName);
    }
}
