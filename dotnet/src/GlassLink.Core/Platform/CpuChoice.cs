using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GlassLink.Core.Platform;

/// <summary>One physical core: its logical CPUs, its efficiency class (higher = faster; the same for all cores of a CPU
/// that is not hybrid) and the last-level (L3) cache it shares: an id per cache and its size in bytes.</summary>
public sealed record CpuCore(int[] Logical, int EfficiencyClass, int Cache, long CacheSize);

/// <summary>The CPUs the DMC keeps to, and why.</summary>
public sealed record CpuSet(int[] Cpus, string Why);

/// <summary>
/// Which CPUs the DMC runs on with process.affinity "auto" (the default): out of the simulator's way, by the CPU's real
/// layout rather than by counting. The DMC needs about a quarter of a core (19-23 % measured with the Fenix, 11 Oct
/// 2026) and runs below normal priority anyway; the set only keeps it off the cores the sim wants most.
/// </summary>
public static class CpuChoice
{
    public static CpuSet Auto(IReadOnlyList<CpuCore> cores)
    {
        var sorted = cores.Where(c => c.Logical.Length > 0).OrderBy(c => c.Logical.Min()).ToList();
        if (sorted.Count == 0)
        {
            return new([], "all CPUs (the CPU's layout could not be read)");
        }

        if (sorted.Select(c => c.EfficiencyClass).Distinct().Count() > 1)
        {
            // a hybrid CPU (Intel P + E cores): Windows already puts background work on the efficiency cores; the
            // performance cores stay the sim's. Low-power island cores count too: below the top class is "efficient".
            var top = sorted.Max(c => c.EfficiencyClass);
            var efficient = sorted.Where(c => c.EfficiencyClass < top).ToList();
            var cpus = efficient.SelectMany(c => c.Logical).Order().ToArray();
            return cpus.Length >= 2
                ? new(cpus, $"the {efficient.Count} efficiency cores; the performance cores stay the sim's")
                : new([], "all CPUs (too few efficiency cores to set apart)");
        }

        if (sorted.Count < 6)
        {
            return new([], $"all CPUs ({sorted.Count} cores are too few to set any apart; below normal priority does it)");
        }

        // A dual-chiplet X3D: one chiplet has the extra (3D V-) cache the sim wants, the other a smaller L3. The DMC
        // goes on the smaller one (AMD's driver parks those cores while a game runs, but threads that may only run
        // there still run). One cache size: the last third of the cores, whole cores with their second threads.
        var sizes = sorted.Select(c => c.CacheSize).Distinct().ToList();
        var x3d = sizes.Count > 1;
        var candidates = x3d ? sorted.Where(c => c.CacheSize == sizes.Min()).ToList() : sorted;
        var n = Math.Min(candidates.Count, Math.Max(2, (int)Math.Round(sorted.Count / 3.0)));
        var chosen = candidates.TakeLast(n).SelectMany(c => c.Logical).Order().ToArray();
        return new(chosen, x3d
            ? $"{n} cores of the chiplet without the extra cache; the sim keeps the cache"
            : $"the last {n} of {sorted.Count} cores");
    }

    /// <summary>The cores of processor group 0 (up to 64 logical CPUs) as Windows reports them; empty if it cannot.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<CpuCore> Read()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationAll, 0, ref length);
        if (length == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationAll, buffer, ref length))
            {
                return [];
            }

            var raw = new byte[length];
            Marshal.Copy(buffer, raw, 0, (int)length);
            return Parse(raw);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX records: the cores (RelationProcessorCore) and the L3 caches
    /// (RelationCache, level 3), each core given the L3 that holds its first logical CPU.</summary>
    public static IReadOnlyList<CpuCore> Parse(ReadOnlySpan<byte> raw)
    {
        var cores = new List<(int[] Logical, int Efficiency)>();
        var caches = new List<(ulong Mask, long Size)>();
        for (var offset = 0; offset + 8 <= raw.Length;)
        {
            var relation = BinaryPrimitives.ReadInt32LittleEndian(raw[offset..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(raw[(offset + 4)..]);
            if (size < 8 || offset + size > raw.Length)
            {
                break;
            }

            var p = raw.Slice(offset + 8, size - 8);
            if (relation == RelationProcessorCore && p.Length >= 40)
            {
                // Flags, EfficiencyClass, Reserved[20], GroupCount, GROUP_AFFINITY GroupMask[] (Mask 8, Group 2, 6 reserved)
                var groups = BinaryPrimitives.ReadUInt16LittleEndian(p[22..]);
                var mask = 0UL;
                for (var g = 0; g < groups && 24 + 16 * g + 10 <= p.Length; g++)
                {
                    if (BinaryPrimitives.ReadUInt16LittleEndian(p[(32 + 16 * g)..]) == 0)
                    {
                        mask |= BinaryPrimitives.ReadUInt64LittleEndian(p[(24 + 16 * g)..]);
                    }
                }

                if (mask != 0)
                {
                    cores.Add((Bits(mask), p[1]));
                }
            }
            else if (relation == RelationCache && p.Length >= 48 && p[0] == 3)
            {
                // Level, Associativity, LineSize, CacheSize, Type, Reserved[18], GroupCount, GROUP_AFFINITY GroupMask[]
                // (older Windows: Reserved[20] and one mask, read there as a group count of 0)
                if (BinaryPrimitives.ReadUInt16LittleEndian(p[40..]) == 0)
                {
                    caches.Add((BinaryPrimitives.ReadUInt64LittleEndian(p[32..]), BinaryPrimitives.ReadUInt32LittleEndian(p[4..])));
                }
            }

            offset += size;
        }

        return [.. cores.Select(c =>
        {
            var cache = caches.FindIndex(l3 => (l3.Mask >> c.Logical.Min() & 1) == 1);
            return new CpuCore(c.Logical, c.Efficiency, cache, cache >= 0 ? caches[cache].Size : 0);
        })];
    }

    private static int[] Bits(ulong mask) => [.. Enumerable.Range(0, 64).Where(i => (mask >> i & 1) == 1)];

    private const int RelationProcessorCore = 0, RelationCache = 2, RelationAll = 0xFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, nint buffer, ref uint returnedLength);
}
