using System.Text.RegularExpressions;
using GlassLink.Core.Config;

namespace GlassLink.Sim;

public sealed record BrightnessStatus(bool Running, string Aircraft, IReadOnlyDictionary<string, string> Map, string? Standdown);

/// <summary>
/// The cockpit's DU brightness knobs for the DUs. The aircraft profile names one variable per display (for the Fenix
/// its effective brightness, 0..1); they are read over SimConnect like any other variable. The pop-out windows
/// themselves are only on or off under the knob, never dimmed, so the DMC applies the value on the DU. It stands
/// down when the aircraft dims its own pop-outs (Fenix "Home Cockpit Mode"), or the picture would be dimmed twice.
/// </summary>
public sealed class BrightnessLink(ConfigFile config, SimConnectClient sim, SimCamera camera)
{
    private readonly Dictionary<string, SimVariable> _variables = [];
    private string _profileFor = "";
    private AircraftProfile? _profile;
    private long _dimmingChecked = long.MinValue / 2;
    private string? _standdown;

    /// <summary>Brightness of a display, 0..1; null when it is not known (no sim, no profile, or standing down).</summary>
    public double? For(string display)
    {
        var profile = Profile();
        if (profile is null || Standdown(profile) is not null || !profile.Brightness.TryGetValue(display, out var name))
        {
            return null;
        }

        lock (_variables)
        {
            if (!_variables.TryGetValue(name, out var variable))
            {
                _variables[name] = variable = sim.Number("L:" + name);
            }

            return variable.Value;
        }
    }

    public BrightnessStatus Status()
    {
        var profile = Profile();
        return new BrightnessStatus(sim.Connected, camera.Title, profile?.Brightness ?? new Dictionary<string, string>(), profile is null ? null : Standdown(profile));
    }

    private AircraftProfile? Profile()
    {
        var title = camera.Title;
        if (title != _profileFor)
        {
            (_profileFor, _profile) = (title, Profiles.Select(config.Snapshot(), title));
        }

        return _profile;
    }

    /// <summary>The name of the aircraft's own dimming mode if it is switched on (looked up every 5 s).</summary>
    private string? Standdown(AircraftProfile profile)
    {
        if (profile.DimmingFile is null || profile.DimmingTag is null)
        {
            return null;
        }

        var now = Environment.TickCount64;
        if (now - _dimmingChecked > 5000)
        {
            _dimmingChecked = now;
            try
            {
                var text = File.ReadAllText(profile.DimmingFile);
                var match = Regex.Match(text, $"<{Regex.Escape(profile.DimmingTag)}>\\s*([^<]*?)\\s*</", RegexOptions.IgnoreCase);
                var on = match.Success && string.Equals(match.Groups[1].Value, profile.DimmingOnValue ?? "true", StringComparison.OrdinalIgnoreCase);
                _standdown = on ? profile.DimmingName ?? "the aircraft dims its pop-outs" : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _standdown = null;
            }
        }

        return _standdown;
    }
}
