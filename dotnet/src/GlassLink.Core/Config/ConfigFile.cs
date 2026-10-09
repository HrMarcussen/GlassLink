using GlassLink.Core.Config;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlassLink.Core.Config;

/// <summary>
/// config.json. Kept as a JSON tree, not as typed classes, so that every key this version does not know (from a newer
/// or an older DMC) survives a save untouched.
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

    /// <summary>True when config.json was empty or broken and the copy of the last good save was used instead.</summary>
    public bool LoadedFromBackup { get; private init; }

    public static ConfigFile Load(string path)
    {
        if (!File.Exists(path))
        {
            // gone, but the copy of the last good save is still there (deleted by hand, or a save cut short): use it
            return File.Exists(path + ".bak") && TryParse(path + ".bak") is { } saved
                ? new ConfigFile(saved, path) { LoadedFromBackup = true }
                : new ConfigFile([], path);
        }

        try
        {
            return new ConfigFile(Parse(path), path);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // A save cut short by a crash leaves an empty or half file: the previous save is kept next to it (#30).
            var backup = path + ".bak";
            if (File.Exists(backup))
            {
                return new ConfigFile(Parse(backup), path) { LoadedFromBackup = true };
            }

            throw new InvalidDataException($"{path} cannot be read ({ex.Message}) and there is no {Path.GetFileName(backup)} to fall back to", ex);
        }
    }

    private static JsonObject Parse(string file) =>
        JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidDataException($"{file} does not hold a JSON object");

    private static JsonObject? TryParse(string file)
    {
        try
        {
            return Parse(file);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>Runs an edit under the lock and saves: to a temporary file flushed to disk, then swapped in with the
    /// previous file kept as config.json.bak, so a crash or a blue screen at any moment leaves a readable file. If
    /// the edit throws or the save fails, the edit is taken back, so memory and file do not disagree (#30).</summary>
    public void Update(Action<JsonObject> edit)
    {
        lock (_gate)
        {
            var before = (JsonObject)Root.DeepClone();
            try
            {
                edit(Root);
            }
            catch
            {
                Restore(before);                             // an edit that gave up halfway leaves nothing behind
                throw;
            }

            if (_path is null)
            {
                return;
            }

            try
            {
                var temp = _path + ".tmp";
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(Root.ToJsonString(WriteOptions));
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(_path))
                {
                    File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, _path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Restore(before);
                throw new IOException($"the configuration could not be saved: {ex.Message}", ex);
            }
        }
    }

    private void Restore(JsonObject before)
    {
        Root.Clear();
        foreach (var (key, value) in before)
        {
            Root[key] = value?.DeepClone();
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
/// <summary>A DU's entry in the configuration. <c>Tiles</c> (display -> position on the DU's screen, in the order the
/// tiles are numbered) puts the DU into tile mode, where <c>Display</c> is ignored; <c>Screen</c> is the HDMI mode
/// the DU is asked for (null = left as it is).</summary>
public sealed record DuSettings(string Display, string Label, int Brightness, int? Rotation, int? Screen, IReadOnlyList<(string Display, int X, int Y)> Tiles)
{
    public static DuSettings From(JsonObject? o) => new(
        o?["display"].Text() ?? "",
        o?["label"].Text() ?? "",
        Math.Clamp(o?["brightness"] is { } b && b.GetValueKind() == JsonValueKind.Number ? (int)b.AsDouble() : 100, 0, 100),
        o?["rotation"] is { } r && r.GetValueKind() == JsonValueKind.Number ? (int)r.AsDouble() : null,
        o?["screen"] is { } sc && sc.GetValueKind() == JsonValueKind.Number ? (int)sc.AsDouble() : null,
        (o?["tiles"] as JsonObject)?.Select(kv => (kv.Key,
            (kv.Value as JsonObject)?["x"] is { } x && x.GetValueKind() == JsonValueKind.Number ? (int)x.AsDouble() : 0,
            (kv.Value as JsonObject)?["y"] is { } y && y.GetValueKind() == JsonValueKind.Number ? (int)y.AsDouble() : 0)).ToList() ?? []);
}
