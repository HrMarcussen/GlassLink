using GlassLink.Core.Platform;

namespace GlassLink.Core.Tests;

public class CpuChoiceTests
{
    private const long MB = 1024 * 1024;

    /// <summary><paramref name="count"/> cores from logical CPU <paramref name="first"/> on, two threads each with
    /// hyperthreading.</summary>
    private static IEnumerable<CpuCore> Cores(int count, bool smt, int first = 0, int efficiency = 0, int cache = 0, long cacheMb = 32) =>
        Enumerable.Range(0, count).Select(i => new CpuCore(smt ? [first + 2 * i, first + 2 * i + 1] : [first + i], efficiency, cache, cacheMb * MB));

    private static int[] Range(int from, int to) => [.. Enumerable.Range(from, to - from + 1)];

    [Fact]
    public void Few_cores_are_not_split_at_all()
    {
        Assert.Empty(CpuChoice.Auto([.. Cores(4, smt: true)]).Cpus);          // 4 cores, 8 threads: priority alone
        Assert.Empty(CpuChoice.Auto([.. Cores(4, smt: false)]).Cpus);
        Assert.Contains("too few", CpuChoice.Auto([.. Cores(4, smt: true)]).Why);
        Assert.Empty(CpuChoice.Auto([]).Cpus);                                 // the layout could not be read
    }

    [Fact]
    public void One_cache_the_last_third_of_the_cores_in_whole_cores()
    {
        Assert.Equal(Range(8, 11), CpuChoice.Auto([.. Cores(6, smt: true)]).Cpus);        // i7-8700K: as before
        Assert.Equal(Range(10, 15), CpuChoice.Auto([.. Cores(8, smt: true, cacheMb: 96)]).Cpus);  // 7800X3D / 9800X3D: one chiplet
        var twoEqual = Cores(8, true, 0, cache: 0).Concat(Cores(8, true, 16, cache: 1)).ToList();
        Assert.Equal(Range(22, 31), CpuChoice.Auto(twoEqual).Cpus);                     // 9950X: two chiplets, same cache
    }

    [Fact]
    public void A_dual_chiplet_X3D_keeps_the_DMC_off_the_chiplet_with_the_extra_cache()
    {
        var x3d = Cores(8, true, 0, cache: 0, cacheMb: 96).Concat(Cores(8, true, 16, cache: 1, cacheMb: 32)).ToList();   // 7950X3D
        var chosen = CpuChoice.Auto(x3d);
        Assert.Equal(Range(22, 31), chosen.Cpus);                               // 5 cores of the plain chiplet
        Assert.Contains("without the extra cache", chosen.Why);

        var cacheSecond = Cores(8, true, 0, cache: 0, cacheMb: 32).Concat(Cores(8, true, 16, cache: 1, cacheMb: 96)).ToList();
        Assert.Equal(Range(6, 15), CpuChoice.Auto(cacheSecond).Cpus);           // whichever chiplet is numbered first

        var x3d12 = Cores(6, true, 0, cache: 0, cacheMb: 96).Concat(Cores(6, true, 12, cache: 1, cacheMb: 32)).ToList();  // 7900X3D
        Assert.Equal(Range(16, 23), CpuChoice.Auto(x3d12).Cpus);                // 4 of the plain chiplet's 6 cores
    }

    [Fact]
    public void A_hybrid_Intel_CPU_gives_the_DMC_its_efficiency_cores()
    {
        var i7 = Cores(8, true, 0, efficiency: 1).Concat(Cores(4, false, 16, efficiency: 0)).ToList();   // i7-12700K
        Assert.Equal(Range(16, 19), CpuChoice.Auto(i7).Cpus);
        Assert.Contains("efficiency cores", CpuChoice.Auto(i7).Why);
        var i9 = Cores(8, true, 0, efficiency: 1).Concat(Cores(16, false, 16, efficiency: 0)).ToList();  // i9-13900K
        Assert.Equal(Range(16, 31), CpuChoice.Auto(i9).Cpus);
        var island = Cores(6, true, 0, efficiency: 2).Concat(Cores(8, false, 12, efficiency: 1)).Concat(Cores(2, false, 20, efficiency: 0)).ToList();
        Assert.Equal(Range(12, 21), CpuChoice.Auto(island).Cpus);              // low-power island cores count as efficient too
    }

    [Fact]
    public void Windows_reports_every_logical_CPU_once_with_its_core_and_cache()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cores = CpuChoice.Read();
        var logical = cores.SelectMany(c => c.Logical).ToList();
        Assert.Equal(Math.Min(64, Environment.ProcessorCount), logical.Count);
        Assert.Equal(logical.Count, logical.Distinct().Count());
    }
}
