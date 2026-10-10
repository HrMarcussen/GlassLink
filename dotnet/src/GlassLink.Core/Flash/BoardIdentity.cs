using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GlassLink.Core.Config;

namespace GlassLink.Core.Flash;

/// <summary>What the chip says about itself in its bootloader: its revision (major * 100 + minor) and factory MAC.</summary>
public sealed record ChipFacts(int Revision, byte[] Mac)
{
    public string RevisionName => $"v{Revision / 100}.{Revision % 100}";

    /// <summary>The serial a DU with this chip reports: the firmware makes it from the factory MAC (main.c
    /// load_or_create_serial, #24), so a board can be recognised before anything is written to it. A unit from before
    /// #24 keeps an older random serial in NVS, which this cannot know.</summary>
    public string DuSerial => BoardIdentity.SerialFromMac(Mac);
}

public static class BoardIdentity
{
    /// <summary>md5("glasslink-du" + MAC), the first 12 bytes in lower-case hex: 24 characters.</summary>
    public static string SerialFromMac(ReadOnlySpan<byte> mac)
    {
        var input = new byte[12 + mac.Length];
        Encoding.ASCII.GetBytes("glasslink-du").CopyTo(input, 0);
        mac.CopyTo(input.AsSpan(12));
        return Convert.ToHexStringLower(MD5.HashData(input).AsSpan(0, 12));
    }

    /// <summary>Which DU of the configuration's "modules" (keyed by serial) a board is: the one that reported this MAC
    /// (INFO "mac", firmware 0.11), else the one whose serial the MAC gives (#24); null for a board never seen. DUs from
    /// before #24 keep an older serial, so they are known only once they have reported their MAC.</summary>
    public static string? KnownSerial(JsonObject? modules, byte[] mac)
    {
        var hex = Convert.ToHexStringLower(mac);
        var derived = SerialFromMac(mac);
        return modules?.FirstOrDefault(kv => (kv.Value as JsonObject)?["mac"].Text() == hex).Key
            ?? (modules?.ContainsKey(derived) == true ? derived : null);
    }
}
