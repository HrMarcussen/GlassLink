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

        // these stopped the DMC at start-up before (review 9 Oct 2026): a port in quotes, a size with a word in it
        Assert.Equal(8765, GlassLink.Dmc.DmcRuntime.PortFrom(JsonNode.Parse("""{"port":"8766"}""")));
        Assert.Equal(8765, GlassLink.Dmc.DmcRuntime.PortFrom(JsonNode.Parse("""{"port":0}""")));
        Assert.Equal(8766, GlassLink.Dmc.DmcRuntime.PortFrom(JsonNode.Parse("""{"port":8766}""")));
        Assert.Equal(8765, GlassLink.Dmc.DmcRuntime.PortFrom(null));
        Assert.Null(JsonNode.Parse("""["768",768]""").Pair());
        Assert.Null(JsonNode.Parse("""[768,null]""").Pair());
        Assert.Equal((768, 784), JsonNode.Parse("""[768,784]""").Pair());
        var profile = GlassLink.Sim.Profiles.Select((JsonObject)JsonNode.Parse("""{"popout":{"profiles":{"Odd":{"points":{"pfd":["x",0.5],"nd":{"xy":[0.6,0.8]}}}}}}""")!, "Odd aircraft");
        Assert.Equal(["nd"], profile!.Points.Keys);                   // the broken point is skipped, the good one kept
    }

    [Fact]
    public void An_edit_that_throws_halfway_leaves_memory_and_file_as_they_were()
    {
        var dir = Directory.CreateTempSubdirectory("glasslink-config").FullName;
        var path = Path.Combine(dir, "config.json");
        try
        {
            File.WriteAllText(path, """{"a":1}""");
            var config = ConfigFile.Load(path);
            Assert.Throws<InvalidOperationException>(() => config.Update(root =>
            {
                root["a"] = 2;
                root["b"] = 3;
                throw new InvalidOperationException("refused halfway");
            }));
            Assert.Equal("""{"a":1}""", config.Root.ToJsonString());
            Assert.Equal("""{"a":1}""", File.ReadAllText(path));

            config.Update(root => root["c"] = 4);                       // a real save: the previous file becomes config.json.bak
            File.Delete(path);                                          // gone, but the last good save is next to it
            var fromBackup = ConfigFile.Load(path);
            Assert.True(fromBackup.LoadedFromBackup);
            Assert.Equal("""{"a":1}""", fromBackup.Root.ToJsonString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
