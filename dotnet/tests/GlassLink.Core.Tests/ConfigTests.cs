using System.Text.Json.Nodes;
using GlassLink.Core.Config;

namespace GlassLink.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void A_save_keeps_the_previous_file_and_a_broken_file_falls_back_to_it()
    {
        var dir = Directory.CreateTempSubdirectory("glasslink-config").FullName;
        var path = Path.Combine(dir, "config.json");
        try
        {
            File.WriteAllText(path, """{"server":{"port":8765},"unknown":{"kept":true}}""");
            var config = ConfigFile.Load(path);
            config.Update(root => ConfigFile.Section(root, "modules")["abc12345"] = new JsonObject { ["label"] = "DU1" });
            Assert.True(File.Exists(path + ".bak"));
            Assert.Contains("DU1", File.ReadAllText(path));
            Assert.Contains("\"kept\"", File.ReadAllText(path));        // keys this version does not know survive

            File.WriteAllText(path, "");                                  // a save cut short by a crash
            var recovered = ConfigFile.Load(path);
            Assert.True(recovered.LoadedFromBackup);
            Assert.Equal(8765, (int)recovered.Root["server"]!["port"]!.AsDouble());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_failed_save_takes_the_edit_back()
    {
        var dir = Directory.CreateTempSubdirectory("glasslink-config").FullName;
        var path = Path.Combine(dir, "config.json");
        try
        {
            File.WriteAllText(path, """{"a":1}""");
            var config = ConfigFile.Load(path);
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))    // another program holds the file
            {
                Assert.Throws<IOException>(() => config.Update(root => root["a"] = 2));
            }

            Assert.Equal(1, (int)config.Root["a"]!.AsDouble());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Odd_types_in_a_hand_edited_file_read_as_defaults()
    {
        var s = DuSettings.From((JsonObject)JsonNode.Parse("""{"display":5,"label":true,"brightness":"x","tiles":{"pfd":7,"nd":{"x":"a","y":3}}}""")!);
        Assert.Equal(("", "", 100), (s.Display, s.Label, s.Brightness));
        Assert.Equal([("pfd", 0, 0), ("nd", 0, 3)], s.Tiles);
    }
}
