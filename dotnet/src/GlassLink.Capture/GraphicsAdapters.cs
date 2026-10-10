using Vortice.DXGI;

namespace GlassLink.Capture;

/// <summary>The graphics adapters as DXGI lists them: for the diagnostics, where the GPU and its memory often explain
/// a slow or frozen display.</summary>
public static class GraphicsAdapters
{
    public static IReadOnlyList<string> Describe()
    {
        var found = new List<string>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    var software = (d.Flags & AdapterFlags.Software) != 0 ? ", software" : "";
                    found.Add($"{d.Description} ({(ulong)d.DedicatedVideoMemory / (1024 * 1024)} MB dedicated{software})");
                }
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            found.Add($"could not list the adapters: {ex.Message}");
        }

        return found;
    }
}
