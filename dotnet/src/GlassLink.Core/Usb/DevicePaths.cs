namespace GlassLink.Core.Usb;

/// <summary>Reading a WinUSB device path. Plain string work, usable on any platform (the tests, CA1416).</summary>
public static class DevicePaths
{
    public static string Serial(string devicePath)
    {
        var parts = devicePath.Split('#');
        return parts.Length >= 3 ? parts[2].ToLowerInvariant() : devicePath.ToLowerInvariant();
    }
}
