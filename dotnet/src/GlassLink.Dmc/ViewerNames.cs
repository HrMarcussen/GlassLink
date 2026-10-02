using System.Text.RegularExpressions;

namespace GlassLink.Dmc;

/// <summary>
/// What a viewer of a display is, in a word the status page can show next to the DUs ("Shown on DU1 · in a browser
/// on iPhone"): taken from the browser's user agent, and "this PC" for one on the sim PC itself.
/// </summary>
public static partial class ViewerNames
{
    public static string Describe(string? userAgent, bool local)
    {
        if (local)
        {
            return "this PC";
        }

        var ua = userAgent ?? "";
        return ua switch
        {
            _ when ua.Contains("iPhone", StringComparison.Ordinal) => "iPhone",
            _ when ua.Contains("iPad", StringComparison.Ordinal) => "iPad",
            _ when ua.Contains("Android", StringComparison.Ordinal) => ua.Contains("Mobile", StringComparison.Ordinal) ? "Android phone" : "Android tablet",
            _ when ua.Contains("websockets", StringComparison.OrdinalIgnoreCase) || ua.StartsWith("Python", StringComparison.OrdinalIgnoreCase) => "Pi viewer",   // pi/viewer.py
            _ when ArmLinux().IsMatch(ua) => "Raspberry Pi",
            _ when ua.Contains("VLC", StringComparison.Ordinal) => "VLC",
            _ when ua.Contains("Windows", StringComparison.Ordinal) => "Windows PC",
            _ when ua.Contains("Macintosh", StringComparison.Ordinal) => "Mac",
            _ when ua.Contains("Linux", StringComparison.Ordinal) => "Linux PC",
            _ => "a device",
        };
    }

    [GeneratedRegex(@"Linux (aarch64|armv\d+l?|arm)", RegexOptions.IgnoreCase)]
    private static partial Regex ArmLinux();
}
