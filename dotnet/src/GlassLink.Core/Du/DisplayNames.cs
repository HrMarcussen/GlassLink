namespace GlassLink.Core.Du;

/// <summary>The cockpit displays' names as the status page shows them (NICE in admin.html); a DU shows them too (#81).</summary>
public static class DisplayNames
{
    private static readonly Dictionary<string, string> Nice = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pfd"] = "Captain PFD", ["nd"] = "Captain ND", ["ecam_upper"] = "Upper ECAM", ["ecam_lower"] = "Lower ECAM",
        ["fo_pfd"] = "FO PFD", ["fo_nd"] = "FO ND",
    };

    public static string For(string name) => Nice.TryGetValue(name, out var nice) ? nice : name;
}
