using GlassLink.Core.Config;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlassLink.Sim;

/// <summary>The camera a click point was recorded in: the pilot seat after a reset, or one of the sim's seat views.</summary>
public sealed record CameraSpec(int? ViewType, int? ViewIndex)
{
    public static readonly CameraSpec PilotReset = new(null, null);

    /// <summary>Measured with the Fenix: view type 1 = pilot views, index 1 = pilot, 4 = copilot.</summary>
    public static readonly CameraSpec Copilot = new(1, 4);

    /// <summary>Points that share a key are clicked in one camera move.</summary>
    public string Key => ViewType is null ? "reset" : $"view:{ViewType}:{ViewIndex}";

    public static CameraSpec From(JsonObject? o) =>
        o?["type"] is { } t && o["index"] is { } i && t.GetValueKind() == JsonValueKind.Number && i.GetValueKind() == JsonValueKind.Number
            ? new CameraSpec((int)t.AsDouble(), (int)i.AsDouble())
            : PilotReset;

    public JsonObject ToJson() => ViewType is null
        ? new JsonObject { ["mode"] = "reset" }
        : new JsonObject { ["mode"] = "view", ["type"] = ViewType, ["index"] = ViewIndex };
}

/// <summary>Where to click for one display: fractions of the sim window's client area, in a given camera, made on a sim
/// window of the shape Aspect (width / height; null = not known).</summary>
public sealed record ClickPoint(double X, double Y, CameraSpec Camera, double? Aspect = null)
{
    /// <summary>True when the point was made on a window of this shape (3 % either way), or its shape is not known. The
    /// cockpit camera shows more or less of the cockpit on another shape, so a point from a 16:9 screen lands somewhere
    /// else on a 21:9 one; at another resolution of the same shape it lands right.</summary>
    public bool Fits(double aspect) => Aspect is not { } a || Math.Abs(a - aspect) <= a * 0.03;

    /// <summary>A screen shape as people name it ("16:9", "21:9"), else as a ratio ("2.10:1").</summary>
    public static string Shape(double aspect)
    {
        (string Name, double Value)[] known = [("16:9", 16.0 / 9), ("16:10", 1.6), ("21:9", 64.0 / 27), ("32:9", 32.0 / 9), ("4:3", 4.0 / 3), ("5:4", 1.25), ("3:2", 1.5)];
        foreach (var (name, value) in known)
        {
            if (Math.Abs(value - aspect) <= value * 0.03)
            {
                return name;
            }
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{aspect:0.00}:1");
    }
}

/// <summary>What the DMC knows about one aircraft: zoom, click points, brightness variables, and whether the aircraft
/// dims its own pop-outs.</summary>
public sealed record AircraftProfile(string Key, double Zoom, IReadOnlyDictionary<string, ClickPoint> Points,
    IReadOnlyDictionary<string, string> Brightness, string? DimmingFile, string? DimmingTag, string? DimmingOnValue, string? DimmingName,
    string? Detect = null, CameraSpec? CopilotCamera = null, bool DimmingAlways = false);

/// <summary>
/// Built-in profiles merged with "popout.profiles" of config.json (the configuration wins, points and displays merge
/// per display). One format for both sims: an MSFS profile applies to an aircraft when its key is part of the aircraft's
/// title and pops out by clicks (points, cameras); an X-Plane profile ("sim": "xplane") applies when its key is part of
/// the aircraft's .acf path and pops out by the aircraft's own commands (displays: window title, command, popup state).
/// </summary>
/// <remarks>
/// The ToLiss profile: the ToLiss Airbus family (A319, A320neo, A321, A339, A340), measured 2 Oct 2026 with the A321
/// 1.7.2 and the A339 1.1 on X-Plane 12.4 (the same window titles and commands in both). With the ISCS options "Use
/// popout windows for popups" and "Save popup config on quit" its popups are ordinary windows; AirbusFBW/PopUp*
/// toggles one, toliss_airbus/reinstatePopups brings back every one that was popped out at the end of the last flight.
/// The windows are drawn sharp at any size inside a 15 px X-Plane frame, and they dim with the cockpit's knobs themselves.
/// </remarks>
public static class Profiles
{
    private const string BuiltIn = """
    {
      "Fenix": {
        "zoom": 30,
        "aspect": 1.7778,
        "detect": "pfd_sphere",
        "points": {
          "pfd": [0.4832, 0.8160], "nd": [0.5805, 0.8090], "ecam_upper": [0.7488, 0.7903], "ecam_lower": [0.7488, 0.9500],
          "fo_nd":  {"xy": [0.4063, 0.8167], "camera": {"mode": "view", "type": 1, "index": 4}},
          "fo_pfd": {"xy": [0.5039, 0.8167], "camera": {"mode": "view", "type": 1, "index": 4}}
        },
        "brightness": {
          "pfd": "N_DISPLAY_BRIGHTNESS_CO", "nd": "N_DISPLAY_BRIGHTNESS_CI", "ecam_upper": "N_DISPLAY_BRIGHTNESS_ECAM_U",
          "ecam_lower": "N_DISPLAY_BRIGHTNESS_ECAM_L", "fo_pfd": "N_DISPLAY_BRIGHTNESS_FO", "fo_nd": "N_DISPLAY_BRIGHTNESS_FI"
        },
        "popout_dimming": {
          "file": "C:\\ProgramData\\Fenix\\FenixSim A320\\persistancy.xml", "xml_tag": "homeCockpitMode", "on_value": "true",
          "name": "Fenix Home Cockpit Mode"
        }
      },
      "FSLabs": {
        "zoom": 30,
        "detect": "pfd_sphere",
        "aspect": 1.7778,
        "copilot_camera": {"mode": "view", "type": 2, "index": 5},
        "points": {
          "pfd": [0.4902, 0.8056], "nd": [0.5902, 0.8056], "ecam_upper": [0.7715, 0.7944], "ecam_lower": [0.7809, 0.9556],
          "fo_nd": [0.9340, 0.8056],
          "fo_pfd": {"xy": [0.5559, 0.7000], "camera": {"mode": "view", "type": 2, "index": 5}}
        },
        "popout_dimming": {"always": true, "name": "the FSLabs dims its pop-outs itself"}
      },
      "ToLiss": {
        "sim": "xplane",
        "displays": {
          "pfd": {"title": "ToLiss Captain Left DU", "command": "AirbusFBW/PopUpPFD1", "state": 2},
          "fo_pfd": {"title": "ToLiss Copilot Right DU", "command": "AirbusFBW/PopUpPFD2", "state": 3},
          "nd": {"title": "ToLiss Captain Right DU", "command": "AirbusFBW/PopUpND1", "state": 4},
          "fo_nd": {"title": "ToLiss Copilot Left DU", "command": "AirbusFBW/PopUpND2", "state": 5},
          "ecam_upper": {"title": "ToLiss Upper ECAM", "command": "AirbusFBW/PopUpEWD", "state": 6},
          "ecam_lower": {"title": "ToLiss Lower ECAM", "command": "AirbusFBW/PopUpSD", "state": 7}
        },
        "reinstate_command": "toliss_airbus/reinstatePopups",
        "state_array": "AirbusFBW/PopUpStateArray",
        "frame": 15,
        "popout_dimming": {"always": true, "name": "the ToLiss dims its pop-outs itself"}
      }
    }
    """;

    /// <summary>An X-Plane profile ("sim": "xplane"); every other one is for MSFS.</summary>
    public static bool IsXPlane(JsonObject profile) => profile["sim"].Text() == "xplane";

    /// <summary>The MSFS profiles.</summary>
    public static IReadOnlyDictionary<string, AircraftProfile> All(JsonObject config)
    {
        var defaultZoom = (config["popout"] as JsonObject)?["zoom"] is { } z && z.GetValueKind() == JsonValueKind.Number ? z.AsDouble() : 30;
        return Merged(config).Where(kv => kv.Value is JsonObject o && !IsXPlane(o))
            .ToDictionary(kv => kv.Key, kv => Parse(kv.Key, (JsonObject)kv.Value!, defaultZoom));
    }

    /// <summary>The built-in profiles of both sims with the configuration's on top, as JSON.</summary>
    public static JsonObject Merged(JsonObject config)
    {
        var merged = (JsonObject)JsonNode.Parse(BuiltIn)!;
        if ((config["popout"] as JsonObject)?["profiles"] is JsonObject user)
        {
            foreach (var (key, node) in user)
            {
                if (node is not JsonObject theirs)
                {
                    continue;
                }

                var ours = merged[key] as JsonObject ?? [];
                merged[key] = ours;
                foreach (var (k, v) in theirs)
                {
                    if (k is "points" or "displays" && v is JsonObject entries)      // one display's point or window, not all of them
                    {
                        var target = ours[k] as JsonObject ?? [];
                        ours[k] = target;
                        foreach (var (name, p) in entries)
                        {
                            target[name] = p?.DeepClone();
                        }
                    }
                    else
                    {
                        ours[k] = v?.DeepClone();
                    }
                }
            }
        }

        return merged;
    }

    /// <summary>The MSFS profile whose key is part of the aircraft title (case-insensitive); the longest key wins.</summary>
    public static AircraftProfile? Select(JsonObject config, string? aircraftTitle) =>
        string.IsNullOrWhiteSpace(aircraftTitle)
            ? null
            : All(config).Values.Where(p => aircraftTitle.Contains(p.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Key.Length).FirstOrDefault();

    private static bool IsCustomCamera(JsonObject point) =>
        point["camera"] is JsonObject camera && camera["mode"] is { } mode && mode.GetValueKind() == JsonValueKind.String && mode.GetValue<string>() == "custom";

    private static AircraftProfile Parse(string key, JsonObject o, double defaultZoom)
    {
        var profileCamera = CameraSpec.From(o["camera"] as JsonObject);
        static double? Number(JsonNode? n) => n is not null && n.GetValueKind() == JsonValueKind.Number && n.AsDouble() > 0 ? n.AsDouble() : null;
        var profileAspect = Number(o["aspect"]);                     // the screen shape the profile's points were made on
        var points = new Dictionary<string, ClickPoint>();
        foreach (var (name, node) in o["points"] as JsonObject ?? [])
        {
            // a hand-edited point that is not two numbers is skipped (Learn again), never thrown: the status page reads profiles
            static (double X, double Y)? Xy(JsonNode? n) =>
                n is JsonArray { Count: 2 } a && a[0].Number(double.NaN) is var x && a[1].Number(double.NaN) is var y && double.IsFinite(x) && double.IsFinite(y) ? (x, y) : null;
            switch (node)
            {
                case JsonArray when Xy(node) is { } xy:
                    points[name] = new ClickPoint(xy.X, xy.Y, profileCamera, profileAspect);
                    break;
                // sim custom cameras ("mode": "custom") were a dead end in 0.4 development builds: such a point is not learned
                case JsonObject p when Xy(p["xy"]) is { } xy && !IsCustomCamera(p):
                    points[name] = new ClickPoint(xy.X, xy.Y,
                        p["camera"] is JsonObject c ? CameraSpec.From(c) : profileCamera, Number(p["aspect"]) ?? profileAspect);
                    break;
            }
        }

        var brightness = (o["brightness"] as JsonObject ?? []).Where(kv => kv.Value.Text() is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Text()!);
        var dim = o["popout_dimming"] as JsonObject;
        return new AircraftProfile(key, o["zoom"] is { } z && z.GetValueKind() == JsonValueKind.Number ? z.AsDouble() : defaultZoom, points, brightness,
            dim?["file"].Text(), dim?["xml_tag"].Text(), dim?["on_value"].Text(), dim?["name"].Text(),
            o["detect"] is { } detect && detect.GetValueKind() == JsonValueKind.String ? detect.GetValue<string>() : null,
            o["copilot_camera"] is JsonObject seat ? CameraSpec.From(seat) : null,    // the FO seat's view, if not the sim's copilot seat
            dim?["always"] is { } always && always.GetValueKind() == JsonValueKind.True);   // the aircraft always dims its pop-outs itself
    }
}
