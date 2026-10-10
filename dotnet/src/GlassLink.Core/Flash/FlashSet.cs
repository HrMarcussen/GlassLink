using System.Globalization;
using GlassLink.Core.Du;

namespace GlassLink.Core.Flash;

/// <summary>One file of a flash set and where it goes in the board's flash.</summary>
public sealed record FlashPart(uint Offset, string Name, byte[] Data);

/// <summary>
/// What "Set up a new board" writes: the files ESP-IDF's build lists in its flash_args (bootloader at 0x2000,
/// partition table at 0x8000, the boot selection at 0xf000, the app at 0x20000), one set per chip family (v1.x and
/// v3.x, #82). The board's NVS (0x9000) is not among them, so a DU that is flashed again keeps its serial and label.
/// </summary>
public sealed record FlashSet(string Folder, IReadOnlyList<FlashPart> Parts, int FlashSize, FirmwareImage App)
{
    /// <summary>Reads a folder with a flash_args file as ESP-IDF writes it ("--flash_size 16MB" or "keep" on the first
    /// line, then "0x2000 bootloader/bootloader.bin" ...). Throws <see cref="InvalidDataException"/> with a message for the user.</summary>
    public static FlashSet Load(string folder)
    {
        var args = Path.Combine(folder, "flash_args");
        if (!File.Exists(args))
        {
            throw new InvalidDataException($"no flash_args in {folder} (build the firmware first)");
        }

        var lines = File.ReadAllLines(args).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var flashSize = 0;
        var parts = new List<FlashPart>();
        FirmwareImage? app = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var i = Array.IndexOf(words, "--flash_size");
                if (i >= 0 && i + 1 < words.Length && words[i + 1].EndsWith("MB", StringComparison.Ordinal)
                    && int.TryParse(words[i + 1][..^2], CultureInfo.InvariantCulture, out var mb))
                {
                    flashSize = mb * 1024 * 1024;
                }

                continue;
            }

            var (offsetText, name) = line.Split(' ', 2, StringSplitOptions.TrimEntries) is [var o, var n] ? (o, n) : throw new InvalidDataException($"flash_args: cannot read '{line}'");
            if (!offsetText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !uint.TryParse(offsetText[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset))
            {
                throw new InvalidDataException($"flash_args: '{offsetText}' is no offset");
            }

            var file = Path.GetFullPath(Path.Combine(folder, name));
            if (!file.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                throw new InvalidDataException($"flash_args names {name}, which is not in {folder}");
            }

            if (name.EndsWith("glasslink_du.bin", StringComparison.OrdinalIgnoreCase))
            {
                app = Firmware.Load(file);                   // a DU app image, its chip range from its header
            }

            parts.Add(new FlashPart(offset, name, File.ReadAllBytes(file)));
        }

        // a signed (release) build says "--flash_size keep": its images must be written unchanged, so the size is the
        // one the bootloader's header carries (byte 3, high nibble: 0 = 1 MB, 4 = 16 MB)
        if (flashSize == 0 && parts.FirstOrDefault(p => p.Name.Contains("bootloader", StringComparison.OrdinalIgnoreCase)) is { Data: [0xE9, _, _, var sizeAndSpeed, ..] })
        {
            flashSize = (1024 * 1024) << (sizeAndSpeed >> 4);
        }

        if (flashSize == 0 || app is null || parts.Count == 0)
        {
            throw new InvalidDataException($"{args} lists no flash size or no GlassLink app");
        }

        var sorted = parts.OrderBy(p => p.Offset).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var end = (long)sorted[i].Offset + sorted[i].Data.Length;
            if (end > flashSize || (i + 1 < sorted.Count && end > sorted[i + 1].Offset))
            {
                throw new InvalidDataException($"flash_args: {sorted[i].Name} overlaps the next part or the end of the flash");
            }
        }

        return new FlashSet(folder, sorted, flashSize, app);
    }
}
