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
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _attempts = [];
    private string _aircraft = "";
    private bool _reinstated;
    private long? _missingSince;
    private long _lastAction = long.MinValue / 2;
    private bool _busy;

    public AutoPopoutState State { get; private set; } = new("starting", "", [], null);

    /// <param name="missing">Names of displays that have no window right now.</param>
    public XPlanePopout(XPlaneClient xplane, Func<IReadOnlyList<string>> missing, Action<string> log)
    {
        (_xplane, _missing, _log) = (xplane, missing, log);
        _timer = new Timer(_ => Tick(), null, 3000, 3000);
    }

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
        }
    }

    public void Dispose() => _timer.Dispose();

    private void Tick()
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

    private void Step()
    {
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

        if (XPlaneProfiles.Select(aircraft) is not { } profile)
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

        var now = Environment.TickCount64;
        _missingSince ??= now;
        if (!_xplane.Knows(profile.Displays[missing[0]].Command))
        {
            State = new("waiting", $"'{_xplane.AircraftName}' is still loading", missing, State.LastAttempt);
            return;
        }

        if (now - _missingSince < 5000 || now - _lastAction < 4000)
        {
            State = new("waiting", "waiting for the pop-out windows", missing, State.LastAttempt);   // they appear a second or two after a command
            return;
        }

        // Right after loading no display has a window: the aircraft brings back all of the last flight's pop-outs at once.
        if (!_reinstated && profile.ReinstateCommand is { } reinstate && !AnyPopout(profile))
        {
            _reinstated = true;
            _lastAction = now;
            var ok = _xplane.Command(reinstate);
            _log($"X-Plane pop-out: {string.Join(", ", missing)} missing -> {reinstate}{(ok ? "" : " (not accepted)")}");
            State = new("running", $"reopening the pop-outs of the last flight ('{_xplane.AircraftName}', profile '{profile.Key}')", missing, DateTime.Now);
            return;
        }

        var states = profile.StateArray is { } array ? _xplane.Numbers(array) : null;
        var opened = new List<string>();
        var inside = new List<string>();
        var givenUp = new List<string>();
        foreach (var name in missing)
        {
            var display = profile.Displays[name];
            var open = states is not null && display.StateIndex < states.Length && states[display.StateIndex] >= 0.5;
            lock (_gate)
            {
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
                    _attempts[name] = attempts + 1;
                    if (_xplane.Command(display.Command))
                    {
                        opened.Add(name);
                    }
                }
            }
        }

        if (opened.Count > 0)
        {
            _lastAction = now;
            _log($"X-Plane pop-out: opening {string.Join(", ", opened)}");
        }

        State = opened.Count > 0
            ? new("running", $"opening {string.Join(", ", opened)} ('{_xplane.AircraftName}', profile '{profile.Key}')", missing, DateTime.Now)
            : inside.Count > 0
                ? new("waiting", $"{string.Join(", ", inside)} open inside X-Plane, not as a window of its own: pop it out once with the button at the right end of its title bar (with the ISCS options \"Use popout windows for popups\" and \"Save popup config on quit\" on, the aircraft remembers it)", inside, State.LastAttempt)
                : givenUp.Count > 0
                    ? new("gave_up", $"gave up on {string.Join(", ", givenUp)}: its popup did not open as a window {MaxAttempts} times. Press Close window to retry", givenUp, State.LastAttempt)
                    : new("waiting", "waiting for the pop-out windows", missing, State.LastAttempt);
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
