using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GlassLink.Capture;
using GlassLink.Core.Config;
using GlassLink.Core.Du;
using GlassLink.Sim;

namespace GlassLink.Dmc;

/// <summary>A request that cannot be carried out; the message is shown to the user.</summary>
public sealed class DisplayException(string message) : Exception(message);

public sealed record DisplayEntry(string Name, FrameSlot Slot, DisplayCapture Capture);

/// <summary>
/// Displays as data: add, change and remove displays while the DMC runs. A display is an entry in the "displays"
/// section of config.json; the registry owns its capture and its frame slot (the slot is kept across a settings
/// change, so a DU that shows the display sees the frame numbers continue).
/// </summary>
public sealed partial class DisplayRegistry(ConfigFile config, Func<string, bool> shownOnDu, Action<string> log) : IDisposable
{
    private const int ParkX0 = 2600, ParkY0 = 0, ParkDx = 800, ParkDy = 820, ParkColumns = 4;
    /// <summary>Picture sizes in whole 16-pixel blocks: the DU's hardware JPEG decoder works in 16 x 16 blocks for the
    /// 4:2:0 pictures the DMC sends and refuses other sizes (#15).</summary>
    // Down to whole 16-pixel blocks, never up: 1080 rows become 1072, which a 1080p DU draws straight into its frame
    // buffer; 1088 would be larger than its screen and refused (#62).
    private static int Round16(int v) => Math.Max(64, v / 16 * 16);

    private static readonly string[] Editable = ["client_size", "position", "fps", "quality", "max_size"];
    private readonly Dictionary<string, DisplayEntry> _entries = [];
    private readonly Dictionary<string, FrameSlot> _slots = [];
    private readonly object _gate = new();

    /// <summary>Told when a display was removed, so DUs that showed it can be unassigned.</summary>
    public Action<string> Removed { get; set; } = _ => { };

    [GeneratedRegex("^[a-z][a-z0-9_]{0,23}$")]
    private static partial Regex NameRule();

    public void StartAll()
    {
        foreach (var name in config.Read(root => (root["displays"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? []))
        {
            Start(name);
        }
    }

    public IReadOnlyList<DisplayEntry> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values];
            }
        }
    }

    public DisplayEntry? Get(string name)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(name);
        }
    }

    public FrameSlot? Slot(string name)
    {
        lock (_gate)
        {
            return name.Length > 0 ? _slots.GetValueOrDefault(name) : null;
        }
    }

    /// <summary>Sim displays (windows of the simulator) that have no window right now.</summary>
    public IReadOnlyList<string> MissingSimDisplays() =>
        All.Where(e => !e.Capture.HasWindow && IsSimDisplay(e.Name)).Select(e => e.Name).ToList();

    public bool IsSimDisplay(string name) =>
        config.Read(root => ((root["displays"] as JsonObject)?[name] as JsonObject)?["match"] is JsonObject m
            && m["process"] is { } p && p.GetValueKind() == JsonValueKind.String
            && string.Equals(p.Text(), PopoutProcedure.SimProcess, StringComparison.OrdinalIgnoreCase));

    public JsonObject Add(string? rawName, JsonObject? fields)
    {
        var name = (rawName ?? "").Trim().ToLowerInvariant();
        if (!NameRule().IsMatch(name))
        {
            throw new DisplayException("name: start with a letter, then letters, digits or _ (max 24), e.g. fo_pfd");
        }

        JsonObject? created = null;
        config.Update(root =>
        {
            var displays = ConfigFile.Section(root, "displays");
            if (displays.ContainsKey(name))
            {
                throw new DisplayException($"display '{name}' already exists");
            }

            created = new JsonObject
            {
                ["match"] = new JsonObject { ["process"] = PopoutProcedure.SimProcess, ["class"] = PopoutProcedure.SimClass, ["title"] = PopoutProcedure.TitlePrefix + name },
                ["client_size"] = new JsonArray(768, 768),
                ["position"] = NextParkingSlot(displays),
            };
            foreach (var (key, value) in Clean(fields))
            {
                if (value is not null)
                {
                    created[key] = value;
                }
            }

            displays[name] = created;
        });
        Start(name);
        log($"display '{name}' added");
        return (JsonObject)created!.DeepClone();
    }

    private readonly object _edit = new();                  // one add / update / remove at a time (#40)

    public JsonObject Update(string name, JsonObject? fields)
    {
        lock (_edit)
        {
            return UpdateLocked(name, fields);
        }
    }

    private JsonObject UpdateLocked(string name, JsonObject? fields)
    {
        JsonObject? result = null;
        var changed = false;
        config.Update(root =>
        {
            if ((root["displays"] as JsonObject)?[name] is not JsonObject display)
            {
                throw new DisplayException($"unknown display '{name}'");
            }

            foreach (var (key, value) in Clean(fields))
            {
                changed = true;
                if (value is null)
                {
                    display.Remove(key);
                }
                else
                {
                    display[key] = value;
                }
            }

            result = (JsonObject)display.DeepClone();
        });
        if (changed)
        {
            Stop(name);                                      // a fresh capture re-applies size, position and rates
            Start(name);
            log($"display '{name}' updated");
        }

        return result!;
    }

    public void Remove(string name)
    {
        lock (_edit)
        {
            RemoveLocked(name);
        }
    }

    private void RemoveLocked(string name)
    {
        config.Update(root =>
        {
            if ((root["displays"] as JsonObject)?.Remove(name) != true)
            {
                throw new DisplayException($"unknown display '{name}'");
            }
        });
        Stop(name);
        lock (_gate)
        {
            _slots.Remove(name);
        }

        Removed(name);
        log($"display '{name}' removed");
    }

    /// <summary>For the editor: the editable fields plus whether a pop-out click point is known for the aircraft.</summary>
    public JsonObject Describe(AircraftProfile? profile)
    {
        var result = new JsonObject();
        config.Read(root =>
        {
            foreach (var (name, node) in root["displays"] as JsonObject ?? [])
            {
                if (node is not JsonObject d)
                {
                    continue;
                }

                var entry = new JsonObject();
                foreach (var key in Editable)
                {
                    entry[key] = d[key]?.DeepClone();
                }

                var point = profile?.Points.GetValueOrDefault(name);
                entry["title"] = (d["match"] as JsonObject)?["title"]?.DeepClone();
                entry["sim_window"] = IsSimDisplay(name);
                entry["has_point"] = point is not null;
                entry["point_view"] = point is null ? null : point.Camera.ViewType is null ? "captain seat" : "FO seat";
                entry["has_window"] = Get(name)?.Capture.HasWindow ?? false;
                result[name] = entry;
            }

            return 0;
        });
        return result;
    }

    public void Dispose()
    {
        foreach (var entry in All)
        {
            entry.Capture.Dispose();
        }
    }

    private void Start(string name)
    {
        var (display, capture) = config.Read(root => (((root["displays"] as JsonObject)?[name] as JsonObject)?.DeepClone() as JsonObject, root["capture"]?.DeepClone() as JsonObject));
        if (display is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_slots.TryGetValue(name, out var slot))
            {
                _slots[name] = slot = new FrameSlot(name) { Title = DisplayNames.For(name) };
            }

            if (_entries.Remove(name, out var old))
            {
                old.Capture.Dispose();                       // never two captures of one display: a leftover one would outlive the DMC
            }

            _entries[name] = new DisplayEntry(name, slot, new DisplayCapture(name, display, capture, slot, () => shownOnDu(name) || slot.Clients > 0, log));
        }
    }

    private void Stop(string name)
    {
        lock (_gate)
        {
            if (_entries.Remove(name, out var entry))
            {
                entry.Capture.Dispose();
            }
        }
    }

    private static JsonArray NextParkingSlot(JsonObject displays)
    {
        var used = displays.Select(kv => (kv.Value as JsonObject)?["position"] as JsonArray).Where(a => a is { Count: 2 })
            .Select(a => ((int)a![0]!.AsDouble(), (int)a[1]!.AsDouble())).ToHashSet();
        for (var i = 0; i < 64; i++)
        {
            var slot = (ParkX0 + i % ParkColumns * ParkDx, ParkY0 + i / ParkColumns * ParkDy);
            if (!used.Contains(slot))
            {
                return new JsonArray(slot.Item1, slot.Item2);
            }
        }

        throw new DisplayException("no free parking slot");
    }

    /// <summary>Validates the editable properties; returns only what may be written to the configuration.</summary>
    private static Dictionary<string, JsonNode?> Clean(JsonObject? fields)
    {
        var result = new Dictionary<string, JsonNode?>();
        foreach (var (key, value) in fields ?? [])
        {
            if (!Editable.Contains(key))
            {
                continue;
            }

            if (value is null || (value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Length == 0))
            {
                result[key] = null;
                continue;
            }

            switch (key)
            {
                case "client_size":                           // multiples of 16: the DU's hardware JPEG decoder takes nothing else
                    var size = PairOf(value, "size", 64, 4096);
                    result[key] = new JsonArray(Round16((int)size[0]!.AsDouble()), Round16((int)size[1]!.AsDouble()));
                    break;
                case "position":
                    result[key] = PairOf(value, "position", -20000, 20000);
                    break;
                case "fps":
                    result[key] = Number(value) is >= 0.2 and <= 60 and var fps ? fps : throw new DisplayException("fps must be between 0.2 and 60");
                    break;
                case "quality":
                    result[key] = Number(value) is >= 30 and <= 100 and var q ? (int)q : throw new DisplayException("quality must be between 30 and 100");
                    break;
                case "max_size":
                    result[key] = Number(value) is >= 64 and <= 4096 and var m ? (int)m : throw new DisplayException("max_size must be between 64 and 4096");
                    break;
            }
        }

        return result;
    }

    private static double Number(JsonNode value) => value.GetValueKind() switch
    {
        JsonValueKind.Number => value.AsDouble(),
        JsonValueKind.String when double.TryParse(value.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => double.NaN,
    };

    private static JsonArray PairOf(JsonNode value, string what, int low, int high)
    {
        if (value is not JsonArray { Count: 2 } a || double.IsNaN(Number(a[0]!)) || double.IsNaN(Number(a[1]!)))
        {
            throw new DisplayException($"{what} must be two whole numbers");
        }

        var (x, y) = ((int)Number(a[0]!), (int)Number(a[1]!));
        return x < low || x > high || y < low || y > high ? throw new DisplayException($"{what} {x},{y} is outside {low}..{high}") : new JsonArray(x, y);
    }
}
