using GlassLink.Core.Config;

namespace GlassLink.Sim;

public sealed record AutoPopoutState(string Status, string Detail, IReadOnlyList<string> Missing, DateTime? LastAttempt);

/// <summary>
/// Watches for displays without a window and pops them out when the sim is in the cockpit of an aircraft it has a
/// profile for. It never keeps moving the user's camera for a click that does not work: after a few attempts that
/// opened no window it gives up on that display until its click point changes or someone asks again.
/// </summary>
public sealed class AutoPopout : IDisposable
{
    private readonly ConfigFile _config;
    private readonly SimCamera _camera;
    private readonly Func<IReadOnlyList<string>> _missing;
    private readonly Action<string> _log;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Fails, ClickPoint? Point)> _fails = [];
    private long? _missingSince;
    private long _lastAttempt = long.MinValue / 2;
    private long _retryAfterMs = 60_000;
    private long? _inCockpitSince;
    private bool _busy;

    /// <summary>An aircraft loaded ready to fly may still be starting its systems for a minute or two (the FSLabs:
    /// about two; the Fenix: seconds), and until then some of its displays open no pop-out at all, dark ones included.
    /// A display that does not pop out in this time after entering the cockpit is tried again sooner and does not
    /// count towards giving up.</summary>
    private const int StartupSeconds = 180, StartupRetrySeconds = 30;

    public AutoPopoutState State { get; private set; } = new("starting", "", [], null);

    /// <summary>Set while a click point is being learned: the camera belongs to the user then.</summary>
    public bool Paused { get; set; }

    /// <param name="missing">Names of sim displays that have no window right now.</param>
    public AutoPopout(ConfigFile config, SimCamera camera, Func<IReadOnlyList<string>> missing, Action<string> log)
    {
        (_config, _camera, _missing, _log) = (config, camera, missing, log);
        _timer = new Timer(_ => Tick(), null, 5000, 5000);
    }

    /// <summary>Forget earlier failures (one display, or all) and try again soon.</summary>
    public void Retry(string? name = null)
    {
        lock (_gate)
        {
            if (name is null)
            {
                _fails.Clear();
            }
            else
            {
                _fails.Remove(name);
            }

            _lastAttempt = long.MinValue / 2;
        }
    }

    private volatile bool _stopping;

    /// <summary>Stops the timer and, if a pop-out is running, lets it stop after the current display and bring the
    /// user's view back before the DMC goes (#39). Waits at most 20 s.</summary>
    public void Dispose()
    {
        _stopping = true;
        _timer.Dispose();
        var deadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < deadline)
        {
            lock (_gate)
            {
                if (!_busy)
                {
                    return;
                }
            }

            Thread.Sleep(100);
        }
    }

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
        catch (Exception ex)                                // a timer callback must never take the process down
        {
            State = new("error", $"{ex.GetType().Name}: {ex.Message}", State.Missing, State.LastAttempt);
            _log($"auto pop-out: {State.Detail}");
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
        var settings = PopoutSettings.From(_config.Snapshot());
        if (Paused)
        {
            State = new("waiting", "paused while a pop-out click point is being learned", State.Missing, State.LastAttempt);
            return;
        }

        var missing = _missing().ToList();
        lock (_gate)
        {
            foreach (var name in _fails.Keys.Where(n => !missing.Contains(n)).ToList())
            {
                _fails.Remove(name);                         // it has a window again
            }
        }

        if (missing.Count == 0)
        {
            _missingSince = null;
            State = new("idle", "all displays have windows", [], State.LastAttempt);
            return;
        }

        if (PopoutProcedure.SimMainWindow() is null)
        {
            _missingSince = null;
            State = new("waiting", "sim not running", missing, State.LastAttempt);
            return;
        }

        var now = Environment.TickCount64;
        _missingSince ??= now;
        if (now - _missingSince < settings.GraceSeconds * 1000)
        {
            State = new("waiting", $"grace period ({settings.GraceSeconds:0} s)", missing, State.LastAttempt);
            return;
        }

        if (now - _lastAttempt < _retryAfterMs)
        {
            State = new("waiting", $"retry in {(_retryAfterMs - (now - _lastAttempt)) / 1000:0} s", missing, State.LastAttempt);
            return;
        }

        if (!_camera.Ready)
        {
            State = new("waiting", SimConnectClient.LibraryFound ? "SimConnect not available yet" : SimConnectClient.LibraryMissing, missing, State.LastAttempt);
            return;
        }

        if (!_camera.InCockpit)
        {
            _inCockpitSince = null;                          // a new flight (loading screen) starts the start-up window again
            State = new("waiting", $"not in cockpit view (aircraft '{_camera.Title}')", missing, State.LastAttempt);
            return;
        }

        _inCockpitSince ??= now;

        if (Profiles.Select(_config.Snapshot(), _camera.Title) is not { } profile)
        {
            State = new("waiting", $"no pop-out profile for aircraft '{_camera.Title}': use Learn on the status page", missing, State.LastAttempt);
            return;
        }

        // A point made on a screen of another shape would miss (a 16:9 profile on a 21:9 screen): it counts as not learned,
        // so the user is asked to Learn it instead of watching clicks that open nothing.
        var aspect = PopoutProcedure.SimMainWindow() is { Client.Height: > 0 } sim ? sim.Client.Width / (double)sim.Client.Height : 0;
        var otherShape = aspect > 0 ? missing.Where(n => profile.Points.TryGetValue(n, out var p) && !p.Fits(aspect)).ToList() : [];
        var unlearned = missing.Where(n => !profile.Points.ContainsKey(n)).Concat(otherShape).ToList();
        List<string> givenUp;
        lock (_gate)
        {
            foreach (var (name, (_, point)) in _fails.ToList())
            {
                if (!Equals(point, profile.Points.GetValueOrDefault(name)))
                {
                    _fails.Remove(name);                     // learned again since: a new chance
                }
            }

            givenUp = missing.Where(n => _fails.TryGetValue(n, out var f) && f.Fails >= settings.MaxAttempts).ToList();
        }

        var todo = missing.Except(unlearned).Except(givenUp).ToList();
        if (todo.Count == 0)
        {
            var madeFor = otherShape.Select(n => profile.Points[n].Aspect).OfType<double>().Select(ClickPoint.Shape).Distinct();
            State = givenUp.Count > 0
                ? new("gave_up", $"gave up on {string.Join(", ", givenUp)}: the click opened no window {settings.MaxAttempts} times. Learn it again, or press Close window to retry", givenUp, State.LastAttempt)
                : otherShape.Count > 0
                    ? new("waiting", $"other screen shape: the click points for {string.Join(", ", otherShape)} were made on a {string.Join("/", madeFor)} screen, the sim window here is {ClickPoint.Shape(aspect)}: use Learn on the status page, once per display", unlearned, State.LastAttempt)
                    : new("waiting", $"no click point yet for {string.Join(", ", unlearned)}: use Learn on the status page", unlearned, State.LastAttempt);
            return;
        }

        _lastAttempt = now;
        State = new("running", $"popping out {string.Join(", ", todo)} ('{_camera.Title}', profile '{profile.Key}')", todo, DateTime.Now);
        _log($"auto pop-out: {string.Join(", ", todo)} missing, aircraft '{_camera.Title}' in cockpit -> popping out with profile '{profile.Key}'");
        if (!CameraLock.TryEnter("the automatic pop-out"))
        {
            State = new("waiting", $"the camera is busy ({CameraLock.Owner}); popping out afterwards", todo, State.LastAttempt);
            return;
        }

        IReadOnlyList<string> done;
        try
        {
            done = new PopoutProcedure(_config, _camera, m => _log($"pop-out: {m}")) { Stop = () => _stopping }.Run(todo, profile);
        }
        finally
        {
            CameraLock.Exit();
        }

        var still = todo.Except(done).ToList();
        var startingUp = _inCockpitSince is { } since && now - since < StartupSeconds * 1000;
        lock (_gate)
        {
            foreach (var name in startingUp ? [] : still)
            {
                _fails[name] = ((_fails.TryGetValue(name, out var f) ? f.Fails : 0) + 1, profile.Points.GetValueOrDefault(name));
            }
        }

        _retryAfterMs = startingUp ? StartupRetrySeconds * 1000 : (long)(settings.RetrySeconds * 1000);
        State = still.Count == 0
            ? new("done", "all displays popped out", [], DateTime.Now)
            : startingUp
                ? new("partial", $"still missing {string.Join(", ", still)}: the aircraft may still be starting up (some take a minute or two before every display pops out); trying again in {StartupRetrySeconds} s", still, DateTime.Now)
                : new("partial", $"still missing {string.Join(", ", still)}, retry in {settings.RetrySeconds:0} s", still, DateTime.Now);
    }
}
