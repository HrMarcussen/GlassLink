using GlassLink.Core.Config;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlassLink.Core.Config;

/// <summary>
/// config.json, shared with the Python DMC. Kept as a JSON tree, not as typed classes, so that every key this
/// version does not know survives a save untouched: both DMCs can work on the same file during the port.
/// </summary>
public sealed class ConfigFile
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, IndentSize = 1 };
    private readonly object _gate = new();
    private readonly string? _path;

    public JsonObject Root { get; }

    public ConfigFile(JsonObject root, string? path = null)
    {
        Root = root;
        _path = path;
    }

    public static ConfigFile Load(string path)
    {
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        return new ConfigFile(root ?? [], path);
    }

    /// <summary>Runs an edit under the lock and writes the file (to a temporary file first, then swapped in).</summary>
    public void Update(Action<JsonObject> edit)
    {
        lock (_gate)
        {
            edit(Root);
            if (_path is null)
            {
                return;
            }

            var temp = _path + ".tmp";
            File.WriteAllText(temp, Root.ToJsonString(WriteOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }

    /// <summary>A copy of the whole tree for a reader on another thread (JsonObject is not thread-safe; the tree is small).</summary>
    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            return (JsonObject)Root.DeepClone();
        }
    }

    public T Read<T>(Func<JsonObject, T> read)
    {
        lock (_gate)
        {
            return read(Root);
        }
    }

    /// <summary>The object at a key, created if missing (or if something else sits there).</summary>
    public static JsonObject Section(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }
}

/// <summary>What the configuration says about one DU (section "modules", keyed by serial).</summary>
public sealed record DuSettings(string Display, string Label, int Brightness, int? Rotation)
{
    public static DuSettings From(JsonObject? o) => new(
        o?["display"]?.GetValue<string>() ?? "",
        o?["label"]?.GetValue<string>() ?? "",
        Math.Clamp(o?["brightness"] is { } b && b.GetValueKind() == JsonValueKind.Number ? (int)b.AsDouble() : 100, 0, 100),
        o?["rotation"] is { } r && r.GetValueKind() == JsonValueKind.Number ? (int)r.AsDouble() : null);
}
