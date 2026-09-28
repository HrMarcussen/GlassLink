using System.Text;
using GlassLink.Dmc;
using GlassLink.Sim;

namespace GlassLink.Core.Tests;

public class LifecycleTests
{
    [Fact]
    public void A_windows_1252_exe_xml_with_duplicates_is_read_and_kept_in_its_encoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var file = Path.Combine(Path.GetTempPath(), $"glasslink-exe-{Guid.NewGuid():N}.xml");
        var cp1252 = Encoding.GetEncoding(1252);
        File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"Windows-1252\"?>\n<SimBase.Document Type=\"Launch\" version=\"1,0\">\n"
            + "  <Launch.Addon><Name>Caf\u00e9 tool</Name><Path>C:/x.exe</Path></Launch.Addon>\n"
            + "  <Launch.Addon><Name>GlassLink DMC</Name><Path>C:/old1.exe</Path></Launch.Addon>\n"
            + "  <Launch.Addon><Name>GlassLink DMC</Name><Path>C:/old2.exe</Path></Launch.Addon>\n</SimBase.Document>", cp1252);
        try
        {
            Assert.True(SimLaunch.IsEnabledIn(file));
            SimLaunch.Set(false, "config.json", file, "GlassLink.exe");                 // every GlassLink entry goes, not only the first
            var text = File.ReadAllText(file, cp1252);
            Assert.DoesNotContain("GlassLink", text);
            Assert.Contains("Caf\u00e9 tool", text);                                   // still Windows-1252: the accent survives
            SimLaunch.Set(true, "config.json", file, "GlassLink.exe");
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file, cp1252), "GlassLink DMC"));
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".before-glasslink");
        }
    }

    [Fact]
    public void Only_one_owner_drives_the_camera()
    {
        Assert.True(CameraLock.TryEnter("the automatic pop-out"));
        try
        {
            Assert.False(CameraLock.TryEnter("learning a click point"));
            Assert.Equal("the automatic pop-out", CameraLock.Owner);
        }
        finally
        {
            CameraLock.Exit();
        }

        Assert.True(CameraLock.TryEnter("learning a click point"));
        CameraLock.Exit();
        Assert.Equal("", CameraLock.Owner);
    }
}
