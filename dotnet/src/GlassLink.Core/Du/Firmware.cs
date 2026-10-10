using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace GlassLink.Core.Du;

public sealed record FirmwareImage(byte[] Data, string Version, string Project, int Size, uint Crc32, string Path, DateTime Modified)
{
    /// <summary>Signed by the release workflow: a DU running released firmware takes nothing else.</summary>
    public bool Signed { get; init; }

    /// <summary>The chip revisions the image runs on, major * 100 + minor, from its header (100..199 for v1.x chips,
    /// 301..399 for v3.x): ESP-IDF's bootloader refuses it on any other, so it is never sent to one (#82).</summary>
    public int MinChipRevision { get; init; }

    public int MaxChipRevision { get; init; } = int.MaxValue;

    /// <summary>Runs on a chip of this revision; a DU that does not say (firmware before 0.11) has a v1.x chip, as only
    /// v1.x images existed then.</summary>
    public bool Fits(int? chipRevision) => (chipRevision ?? 100) is var r && r >= MinChipRevision && r <= MaxChipRevision;

    /// <summary>"v1.00-v1.99".</summary>
    public string ChipRange => $"v{MinChipRevision / 100}.{MinChipRevision % 100:00}-v{MaxChipRevision / 100}.{MaxChipRevision % 100:00}";
}

/// <summary>DU firmware images: reads the ESP-IDF application descriptor so that only a real DU image is ever sent.</summary>
public static class Firmware
{
    public const string ProjectName = "glasslink_du";
    public const int MaxImage = 0x400000;                   // the update slots on a DU are 4 MiB
    private const int DescriptorOffset = 32;                // image header (24) + first segment header (8)
    private const uint DescriptorMagic = 0xABCD5432;

    /// <summary>Throws <see cref="InvalidDataException"/> with a message for the user if this is not a DU image.</summary>
    public static FirmwareImage Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"no firmware image at {path} (build the firmware first)");
        }

        var data = File.ReadAllBytes(path);
        if (data.Length < DescriptorOffset + 256 || data[0] != 0xE9)
        {
            throw new InvalidDataException("not an ESP application image");
        }

        if (data.Length > MaxImage)
        {
            throw new InvalidDataException($"image is {data.Length} bytes, the update slot of a DU holds {MaxImage}");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(DescriptorOffset)) != DescriptorMagic)
        {
            throw new InvalidDataException("application descriptor not found");
        }

        string Text(int offset, int length)
        {
            var span = data.AsSpan(DescriptorOffset + offset, length);
            var end = span.IndexOf((byte)0);
            return Encoding.ASCII.GetString(end >= 0 ? span[..end] : span);
        }

        var project = Text(48, 32);
        if (project != ProjectName)
        {
            throw new InvalidDataException($"image is for project '{project}', expected '{ProjectName}'");
        }

        return new FirmwareImage(data, Text(16, 32), project, data.Length, Crc32.HashToUInt32(data), path, File.GetLastWriteTime(path))
        {
            Signed = IsSigned(data),
            MinChipRevision = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(15)),       // esp_image_header_t.min_chip_rev_full
            MaxChipRevision = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(17)),       // .max_chip_rev_full
        };
    }

    /// <summary>"v1.3" for 103.</summary>
    public static string ChipName(int revision) => $"v{revision / 100}.{revision % 100}";

    /// <summary>The image ends in an ESP-IDF signature sector (espsecure sign_data --version 2): the image padded to whole
    /// 4 KB, then a 4 KB sector starting with the signature block's magic 0xE7 and version 2 (RSA-3072).</summary>
    public static bool IsSigned(ReadOnlySpan<byte> data) =>
        data.Length >= 2 * 4096 && data.Length % 4096 == 0 && data[^4096] == 0xE7 && data[^4095] == 0x02;

    /// <summary>"0.5.0" -> comparable; anything that is not a digit is ignored ("0.5.0-dirty" = 0.5.0).</summary>
    /// <summary>The leading number of each dot-separated part: "1.0.0-rc2" is 1.0.0 (the digits after the dash must not
    /// make it 1.0.2), "0.9.0" is 0.9.0.</summary>
    public static int[] VersionTuple(string version) =>
        version.Split('.').Select(part => int.TryParse(new string(part.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();

    /// <summary>True if a DU reports firmware older than the release in which the firmware last changed.</summary>
    public static bool IsOutdated(string? duVersion, string firmwareVersion)
    {
        if (string.IsNullOrEmpty(duVersion))
        {
            return false;
        }

        var (a, b) = (VersionTuple(duVersion), VersionTuple(firmwareVersion));
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var (x, y) = (i < a.Length ? a[i] : 0, i < b.Length ? b[i] : 0);
            if (x != y)
            {
                return x < y;
            }
        }

        return false;
    }
}
