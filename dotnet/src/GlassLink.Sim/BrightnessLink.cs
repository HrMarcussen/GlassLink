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
        if (profile.DimmingAlways)
        {
            // the FSLabs: its pop-outs get darker with the cockpit knob (measured 30 Sept 2026: the picture's brightness
            // follows the knob down to black), so dimming on the DU as well would dim twice
            return profile.DimmingName ?? "the aircraft dims its pop-outs";
        }

        if (profile.DimmingFile is null || profile.DimmingTag is null)
        {
            return null;
        }

        var now = Environment.TickCount64;
        if (now - _dimmingChecked > 5000)
        {
            _dimmingChecked = now;
            _standdown = DimmingOn(profile.DimmingFile, profile.DimmingTag, profile.DimmingOnValue, profile.DimmingName ?? "the aircraft dims its pop-outs",
                _standdown, out var busy);
            if (busy)
            {
                _dimmingChecked = now - 4000;                // looked at again in a second
            }
        }

        return _standdown;
    }

    /// <summary>Whether the aircraft's settings file says its own dimming is on: <paramref name="name"/> if so, null if
    /// not or if there is no such file. While the aircraft is writing the file it cannot be read: then the previous answer
    /// stands (<paramref name="busy"/>), so the DU does not dim on top of the aircraft for a few seconds (review C10).</summary>
    public static string? DimmingOn(string file, string tag, string? onValue, string name, string? previous, out bool busy)
    {
        busy = false;
        try
        {
            var text = File.ReadAllText(file);
            var match = Regex.Match(text, $"<{Regex.Escape(tag)}>\\s*([^<]*?)\\s*</", RegexOptions.IgnoreCase);
            return match.Success && string.Equals(match.Groups[1].Value, onValue ?? "true", StringComparison.OrdinalIgnoreCase) ? name : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            busy = true;
            return previous;
        }
    }
}
