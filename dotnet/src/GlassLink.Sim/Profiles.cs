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

/// <summary>Where to click for one display: fractions of the sim window's client area, in a given camera.</summary>
public sealed record ClickPoint(double X, double Y, CameraSpec Camera);

/// <summary>What the DMC knows about one aircraft: zoom, click points, brightness variables, and whether the aircraft
/// dims its own pop-outs.</summary>
public sealed record AircraftProfile(string Key, double Zoom, IReadOnlyDictionary<string, ClickPoint> Points,
    IReadOnlyDictionary<string, string> Brightness, string? DimmingFile, string? DimmingTag, string? DimmingOnValue, string? DimmingName);

/// <summary>
/// Built-in profiles merged with "popout.profiles" of config.json (the configuration wins, points merge per display).
/// A profile applies to an aircraft when its key is part of the aircraft's title. Same format as the Python DMC.
/// </summary>
public static class Profiles
{
    private const string BuiltIn = """
    {
      "Fenix": {
        "zoom": 30,
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
      }
    }
    """;

    public static IReadOnlyDictionary<string, AircraftProfile> All(JsonObject config)
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
                    if (k == "points" && v is JsonObject points)
                    {
                        var target = ours["points"] as JsonObject ?? [];
                        ours["points"] = target;
                        foreach (var (name, p) in points)
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

        var defaultZoom = (config["popout"] as JsonObject)?["zoom"] is { } z && z.GetValueKind() == JsonValueKind.Number ? z.AsDouble() : 30;
        return merged.Where(kv => kv.Value is JsonObject).ToDictionary(kv => kv.Key, kv => Parse(kv.Key, (JsonObject)kv.Value!, defaultZoom));
    }

    /// <summary>The profile whose key is part of the aircraft title (case-insensitive); the longest key wins.</summary>
    public static AircraftProfile? Select(JsonObject config, string? aircraftTitle) =>
        string.IsNullOrWhiteSpace(aircraftTitle)
            ? null
            : All(config).Values.Where(p => aircraftTitle.Contains(p.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Key.Length).FirstOrDefault();

    private static bool IsCustomCamera(JsonObject point) =>
        point["camera"] is JsonObject camera && camera["mode"] is { } mode && mode.GetValueKind() == JsonValueKind.String && mode.GetValue<string>() == "custom";

    private static AircraftProfile Parse(string key, JsonObject o, double defaultZoom)
    {
        var profileCamera = CameraSpec.From(o["camera"] as JsonObject);
        var points = new Dictionary<string, ClickPoint>();
        foreach (var (name, node) in o["points"] as JsonObject ?? [])
        {
            switch (node)
            {
                case JsonArray { Count: 2 } a:
                    points[name] = new ClickPoint(a[0]!.AsDouble(), a[1]!.AsDouble(), profileCamera);
                    break;
                // sim custom cameras ("mode": "custom") were a dead end in 0.4 development builds: such a point is not learned
                case JsonObject p when p["xy"] is JsonArray { Count: 2 } xy && !IsCustomCamera(p):
                    points[name] = new ClickPoint(xy[0]!.AsDouble(), xy[1]!.AsDouble(),
                        p["camera"] is JsonObject c ? CameraSpec.From(c) : profileCamera);
                    break;
            }
        }

        var brightness = (o["brightness"] as JsonObject ?? []).Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
        var dim = o["popout_dimming"] as JsonObject;
        return new AircraftProfile(key, o["zoom"] is { } z && z.GetValueKind() == JsonValueKind.Number ? z.AsDouble() : defaultZoom, points, brightness,
            dim?["file"]?.GetValue<string>(), dim?["xml_tag"]?.GetValue<string>(), dim?["on_value"]?.GetValue<string>(), dim?["name"]?.GetValue<string>());
    }
}
