using System.Globalization;
using System.Text.Json.Nodes;

namespace GlassLink.Core.Config;

public static class JsonNumbers
{
    /// <summary>A JSON string's text; null for anything else (a hand-edited 5 or true must not throw, #29).</summary>
    public static string? Text(this JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>
    /// A JSON number as a double, wherever it came from. GetValue&lt;double&gt;() only works for numbers that were parsed
    /// from text; a number this program put into the tree itself (an int position, a brightness) makes it throw.
    /// Both kinds live in the same configuration tree, so every numeric read goes through here.
    /// </summary>
    public static double AsDouble(this JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d))
            {
                return d;
            }

            if (value.TryGetValue<long>(out var l))
            {
                return l;
            }

            if (value.TryGetValue<int>(out var i))
            {
                return i;
            }

            if (value.TryGetValue<decimal>(out var m))
            {
                return (double)m;
            }
        }

        return double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);
    }

    /// <summary>A JSON number, or <paramref name="fallback"/> for anything else (missing, a string "8766", true): for
    /// values a user may edit by hand, which must never stop the DMC from starting.</summary>
    public static double Number(this JsonNode? node, double fallback) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? v.AsDouble() : fallback;

    /// <summary>Two JSON numbers in an array ([768, 768]) as whole numbers; null for anything else.</summary>
    public static (int A, int B)? Pair(this JsonNode? node) =>
        node is JsonArray { Count: 2 } a && a[0].Number(double.NaN) is var x && a[1].Number(double.NaN) is var y && double.IsFinite(x) && double.IsFinite(y)
            ? ((int)x, (int)y) : null;
}
