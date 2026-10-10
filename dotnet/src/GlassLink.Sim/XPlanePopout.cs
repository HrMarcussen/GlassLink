namespace GlassLink.Sim;

/// <summary>
/// The automatic pop-out for X-Plane. Its aircraft profiles open their displays by command, so nothing moves the camera
/// or clicks in the cockpit: after a flight is loaded the aircraft is asked to bring back the pop-outs of the last
/// flight; a display still without a window has its popup opened on its own, a few times at most. A popup that is open
/// inside X-Plane instead of as a window of its own cannot be popped out by command: the user does that once, and the
/// aircraft remembers it.
/// </summary>
public sealed class XPlanePopout : IDisposable
{
    private const int MaxAttempts = 3;
    private readonly XPlaneClient _xplane;
    private readonly Func<IReadOnlyList<string>> _missing;
    private readonly Action<string> _log;
    private readonly Func<long> _clock;
    private readonly Func<XPlaneProfile, bool> _anyPopout;
    private readonly Timer? _timer;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _attempts = [];
    private string _aircraft = "";
    private bool _reinstated;
    private long? _missingSince;
    private long _lastAction = long.MinValue / 2;
    private bool _busy;

    public AutoPopoutState State { get; private set; } = new("starting", "", [], null);

    /// <param name="missing">Names of displays that have no window right now.</param>
    /// <param name="clock">For tests: milliseconds, as Environment.TickCount64.</param>
    /// <param name="anyPopout">For tests: whether any of the profile's pop-out windows is open.</param>
    /// <param name="timer">For tests: false = no timer; <see cref="Tick"/> is called by hand.</param>
    /// <param name="enabled">Whether automatic pop-out is switched on ("popout.auto"); null = always.</param>
    /// <param name="profiles">The profile for an aircraft path (built-in and config.json); null = the built-in ones.</param>
    public XPlanePopout(XPlaneClient xplane, Func<IReadOnlyList<string>> missing, Action<string> log,
        Func<long>? clock = null, Func<XPlaneProfile, bool>? anyPopout = null, bool timer = true, Func<bool>? enabled = null,
        Func<string, XPlaneProfile?>? profiles = null)
    {
        _profiles = profiles ?? (path => XPlaneProfiles.Select(new System.Text.Json.Nodes.JsonObject(), path));
        (_xplane, _missing, _log) = (xplane, missing, log);
        _clock = clock ?? (() => Environment.TickCount64);
        _anyPopout = anyPopout ?? AnyPopout;
        _enabled = enabled ?? (() => true);
        _timer = timer ? new Timer(_ => Tick(), null, 3000, 3000) : null;
    }

    private readonly Func<bool> _enabled;
    private readonly Func<string, XPlaneProfile?> _profiles;
    private long _askedUntil = long.MinValue / 2;

    /// <summary>Forget earlier attempts (one display, or all) and try again.</summary>
    public void Retry(string? name = null)
    {
        lock (_gate)
        {
            if (name is null)
            {
                _attempts.Clear();
                _reinstated = false;
            }
            else
            {
                _attempts.Remove(name);
            }

            _lastAction = long.MinValue / 2;
            _askedUntil = _clock() + 60_000;               // asked for: also while switched off, for a minute
        }
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>One look (every 3 s): never two at once, never an exception out of the timer.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
        }

        try
        {
            Step();
        }
        catch (Exception ex)                                 // a timer callback must never take the process down
        {
            State = new("error", $"{ex.GetType().Name}: {ex.Message}", State.Missing, State.LastAttempt);
            _log($"X-Plane pop-out: {State.Detail}");
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }
    }

    // X-Plane is asked outside _gate (each request can wait 2 s for a busy X-Plane): Retry and the status page do not wait.
    private void Step()
    {
        if (!_enabled() && _clock() > _askedUntil)
        {
            State = new("off", "automatic pop-out is switched off (tray menu or Setup tab); Pop out missing displays now still works", _missing(), State.LastAttempt);
            return;
        }

        if (!_xplane.Connected)
        {
            Reset("");
            State = new("waiting", "X-Plane not running", [], State.LastAttempt);
            return;
        }

        var aircraft = _xplane.AircraftPath;
        if (aircraft != _aircraft)
        {
            Reset(aircraft);                                 // another flight or aircraft: start over
        }

        if (_profiles(aircraft) is not { } profile)
        {
            State = new("waiting", aircraft.Length == 0 ? "X-Plane: no aircraft loaded" : $"no X-Plane profile for aircraft '{_xplane.AircraftName}'", [], State.LastAttempt);
            return;
        }

        var missing = _missing().Where(profile.Displays.ContainsKey).ToList();
        lock (_gate)
        {
            foreach (var name in _attempts.Keys.Where(n => !missing.Contains(n)).ToList())
            {
                _attempts.Remove(name);                      // it has a window again
            }
        }

        if (missing.Count == 0)
        {
            _missingSince = null;
            State = new("idle", "all displays have windows", [], State.LastAttempt);
            return;
        }

        var now = _clock();
        _missingSince ??= now;
        if (!_xplane.Knows(profile.Displays[missing[0]].Command))
        {
            State = new("waiting", $"'{_xplane.AircraftName}' is still loading", missing, State.LastAttempt);
            return;
        }

        long lastAction;
        bool reinstated;
        lock (_gate)
        {
            (lastAction, reinstated) = (_lastAction, _reinstated);
        }

        if (now - _missingSince < 5000 || now - lastAction < 4000)
        {
            State = new("waiting", "waiting for the pop-out windows", missing, State.LastAttempt);   // they appear a second or two after a command
            return;
        }

        // Right after loading no display has a window: the aircraft brings back all of the last flight's pop-outs at once.
        if (!reinstated && profile.ReinstateCommand is { } reinstate && !_anyPopout(profile))
        {
            var ok = _xplane.Command(reinstate);
            lock (_gate)
            {
                _lastAction = now;
                _reinstated = ok;                            // a command X-Plane did not take (busy loading) is sent again
            }

            _log($"X-Plane pop-out: {string.Join(", ", missing)} missing -> {reinstate}{(ok ? "" : " (not accepted, again in a moment)")}");
            State = new("running", $"reopening the pop-outs of the last flight ('{_xplane.AircraftName}', profile '{profile.Key}')", missing, DateTime.Now);
            return;
        }

        // The popups' states decide between "open it" and "it is open, but inside X-Plane". Without them (X-Plane busy)
        // nothing is sent: a toggle command on a popup that is open would close it.
        var states = profile.StateArray is { } array ? _xplane.Numbers(array) : null;
        if (profile.StateArray is not null && states is null)
        {
            State = new("waiting", "X-Plane is busy: asking again in a moment", missing, State.LastAttempt);
            return;
        }

        var toOpen = new List<string>();
        var inside = new List<string>();
        var givenUp = new List<string>();
        lock (_gate)
        {
            foreach (var name in missing)
            {
                var display = profile.Displays[name];
                var open = states is not null && display.StateIndex >= 0 && display.StateIndex < states.Length && states[display.StateIndex] >= 0.5;
                var attempts = _attempts.GetValueOrDefault(name);
                if (open && attempts > 0)
                {
                    inside.Add(name);                        // open, yet no window of its own: popped up inside X-Plane
                }
                else if (attempts >= MaxAttempts)
                {
                    givenUp.Add(name);
                }
                else if (open)
                {
                    _attempts[name] = 1;                     // look again once before calling it a popup inside X-Plane
                }
                else
                {
                    toOpen.Add(name);
                }
            }
        }

        var opened = toOpen.Where(name => _xplane.Command(profile.Displays[name].Command)).ToList();
        lock (_gate)
        {
            foreach (var name in opened)
            {
                _attempts[name] = _attempts.GetValueOrDefault(name) + 1;     // only a command X-Plane took counts as a try
            }

            if (opened.Count > 0)
            {
                _lastAction = now;
            }
        }

        if (opened.Count > 0)
        {
            _log($"X-Plane pop-out: opening {string.Join(", ", opened)}");
        }

        State = opened.Count > 0
            ? new("running", $"opening {string.Join(", ", opened)} ('{_xplane.AircraftName}', profile '{profile.Key}')", missing, DateTime.Now)
            : inside.Count > 0
                ? new("waiting", $"{string.Join(", ", inside)} open inside X-Plane, not as a window of its own: pop it out once with the button at the right end of its title bar (with the ISCS options \"Use popout windows for popups\" and \"Save popup config on quit\" on, the aircraft remembers it)", inside, State.LastAttempt)
                : givenUp.Count > 0
                    ? new("gave_up", $"gave up on {string.Join(", ", givenUp)}: its popup did not open as a window {MaxAttempts} times. Press Close window to retry", givenUp, State.LastAttempt)
                    : new("waiting", toOpen.Count > 0 ? "X-Plane did not take the command: asking again in a moment" : "waiting for the pop-out windows", missing, State.LastAttempt);
    }

    /// <summary>Whether any of the profile's pop-out windows is open (also one the DMC does not capture).</summary>
    private static bool AnyPopout(XPlaneProfile profile)
    {
        var titles = profile.Displays.Values.Select(d => d.Title).ToHashSet();
        return GlassLink.Capture.Windows.WindowFinder.Enumerate()
            .Any(w => w.ClassName == XPlaneProfiles.WindowClass && string.Equals(w.Process, XPlaneClient.Process, StringComparison.OrdinalIgnoreCase) && titles.Contains(w.Title));
    }

    private void Reset(string aircraft)
    {
        lock (_gate)
        {
            (_aircraft, _reinstated, _missingSince) = (aircraft, false, null);
            _attempts.Clear();
        }
    }
}
