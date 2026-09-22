using System.Text.Json.Nodes;
using GlassLink.Core.Config;
using GlassLink.Core.Du;
using GlassLink.Core.Protocol;

namespace GlassLink.Core.Tests;

public class DuManagerTests
{
    private const string Serial = "aabbccddeeff001122334455";
    private static readonly string Path = $@"\\?\usb#vid_303a&pid_4001#{Serial}#{{b7e8a4c2-6f0d-4e21-9c3a-5d2f1e8b7a60}}";

    private static void Until(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
        }

        Assert.True(condition(), "timed out");
    }

    private static (DuManager Manager, List<FakeDu> Dus, Dictionary<string, FrameSlot> Displays, ConfigFile Config, List<string> Plugged) Make(string json)
    {
        var config = new ConfigFile((JsonObject)JsonNode.Parse(json.Replace("SERIAL", Serial))!);
        var displays = new Dictionary<string, FrameSlot> { ["pfd"] = new("pfd"), ["nd"] = new("nd") };
        var dus = new List<FakeDu>();
        var plugged = new List<string> { Path };
        var manager = new DuManager(config, name => displays.GetValueOrDefault(name), () => plugged.ToList(), _ =>
        {
            var du = new FakeDu();
            dus.Add(du);
            return du;
        });
        return (manager, dus, displays, config, plugged);
    }

    [Fact]
    public void A_du_with_tiles_gets_a_layout_and_each_tile_its_own_frames()
    {
        var (manager, dus, displays, config, _) = Make("""{"modules":{"SERIAL":{"label":"HDMI","screen":3,"tiles":{"pfd":{"x":0,"y":0},"nd":{"x":381,"y":0},"ecam_upper":{"x":0,"y":700}}}}}""");
        using var _m = manager;
        manager.DisplaySize = name => name == "pfd" ? (384, 384) : (384, 376);
        manager.ScanOnce();
        var deadline = Environment.TickCount64 + 3000;
        while (dus[0].Of(MessageType.SetLayout).Count == 0 && Environment.TickCount64 < deadline)
        {
            manager.ScanOnce();                                      // the sync pass once INFO is in: the mode fits (3), so the layout goes
            Thread.Sleep(5);
        }

        var conn = manager.Connection(Serial)!;
        Assert.True(dus[0].Of(MessageType.SetLayout).Count == 1, $"received: {string.Join(",", dus[0].Received.Select(r => r.Type))}; info {conn.Info is not null}, mode {conn.Mode}, tiles {conn.SupportsTiles}, alive {conn.Alive}, settings tiles {manager.Settings(Serial).Tiles.Count}");
        var layout = dus[0].Of(MessageType.SetLayout)[0].Payload.ToArray();
        Assert.Equal(16, layout.Length);
        Assert.Equal((0, 0, 384, 384), (BitConverter.ToUInt16(layout, 0), BitConverter.ToUInt16(layout, 2), BitConverter.ToUInt16(layout, 4), BitConverter.ToUInt16(layout, 6)));
        Assert.Equal((381, 0, 384, 376), (BitConverter.ToUInt16(layout, 8), BitConverter.ToUInt16(layout, 10), BitConverter.ToUInt16(layout, 12), BitConverter.ToUInt16(layout, 14)));   // the position as given, the size a multiple of 8
        Assert.Contains("ecam_upper", manager.Status().Single().LayoutProblem);                     // 0,700 + 376 does not fit the 768x768 fake: left out, reported
        Assert.Empty(dus[0].Of(MessageType.SetMode));

        displays["nd"].Publish(new byte[] { 2 });
        Until(() => dus[0].Of(MessageType.Tile).Count == 1);
        Assert.Equal((1u, 2), (dus[0].Of(MessageType.Tile)[0].Arg, (int)dus[0].Of(MessageType.Tile)[0].Payload.Span[0]));
        displays["pfd"].Publish(new byte[] { 1 });
        Until(() => dus[0].Of(MessageType.Tile).Count == 2);
        Assert.Equal(0u, dus[0].Of(MessageType.Tile)[1].Arg);
        Assert.Empty(dus[0].Of(MessageType.Frame));                  // never a plain frame in tile mode
        Assert.True(manager.IsShown("pfd") && manager.IsShown("nd"));

        manager.ShowCards(Serial, true);
        Until(() => dus[0].Of(MessageType.SetLayout).Count == 2);
        Assert.Equal(1u, dus[0].Of(MessageType.SetLayout)[1].Arg);
        var row = manager.Status().Single();
        Assert.True(row.Cards);
        Assert.Equal(2, row.Layout.Count);

        manager.SetTiles(Serial, null);                              // a single-display DU again
        Until(() => dus[0].Of(MessageType.SetLayout).Count == 3);
        Assert.Equal(0, dus[0].Of(MessageType.SetLayout)[2].Payload.Length);
        Assert.Null(((JsonObject)config.Root["modules"]![Serial]!)["tiles"]);
    }

    [Fact]
    public void A_plugged_in_du_gets_its_display_from_the_configuration()
    {
        var (manager, dus, displays, _, _) = Make("""{"modules":{"SERIAL":{"display":"nd","label":"DU2","brightness":80}},"other":{"kept":true}}""");
        using var _m = manager;
        manager.ScanOnce();
        displays["nd"].Publish(new byte[] { 7 });
        Until(() => dus[0].Of(MessageType.Frame).Count == 1);
        Assert.Equal(7, dus[0].Of(MessageType.Frame)[0].Payload.Span[0]);

        var row = manager.Status().Single();
        Assert.Equal((Serial, "DU2", "nd", 80, true), (row.Serial, row.Label, row.Display, row.Trim, row.Alive));
        manager.ScanOnce();
        Assert.Single(dus);                                         // already connected: not opened twice
    }

    [Fact]
    public void Reassigning_switches_the_picture_and_saves_without_losing_unknown_keys()
    {
        var (manager, dus, displays, config, _) = Make("""{"modules":{"SERIAL":{"display":"pfd"}},"popout":{"zoom":30}}""");
        using var _m = manager;
        manager.ScanOnce();
        displays["pfd"].Publish(new byte[] { 1 });
        Until(() => dus[0].Of(MessageType.Frame).Count == 1);
        displays["nd"].Publish(new byte[] { 2 });
        manager.Assign(Serial, display: "nd", label: " Captain ND ");
        Until(() => dus[0].Of(MessageType.Frame).Count == 2);
        Assert.Equal(2, dus[0].Of(MessageType.Frame)[1].Payload.Span[0]);
        Assert.Equal("Captain ND", manager.Settings(Serial).Label);
        Assert.Equal(30, config.Root["popout"]!["zoom"]!.GetValue<int>());

        manager.Assign(Serial, display: "");                        // not assigned: the DU is told, and gets no frames
        Until(() => dus[0].Of(MessageType.SetAssigned).Any(m => m.Arg == 0));
        manager.DisplayRemoved("nd");
        Assert.Equal("", manager.Settings(Serial).Display);
    }

    [Fact]
    public void Brightness_is_the_cockpit_knob_times_the_trim_and_is_sent_only_when_it_changes()
    {
        var (manager, dus, _, _, _) = Make("""{"modules":{"SERIAL":{"display":"pfd","brightness":80}}}""");
        using var _m = manager;
        manager.ScanOnce();
        Until(() => manager.Connection(Serial)?.Info is not null);
        manager.BrightnessTick();
        manager.BrightnessTick();
        Until(() => dus[0].Of(MessageType.SetBrightness).Count == 1);
        Assert.Equal(80u, dus[0].Of(MessageType.SetBrightness)[0].Arg);         // no knob known: the trim alone

        manager.SimBrightness = display => display == "pfd" ? 0.25 : null;
        manager.BrightnessTick();
        manager.BrightnessTick();
        Until(() => dus[0].Of(MessageType.SetBrightness).Count == 2);
        Assert.Equal(20u, dus[0].Of(MessageType.SetBrightness)[1].Arg);
        Assert.Equal(0.25, manager.Status().Single().BrightnessSim);
    }

    [Fact]
    public void An_unplugged_du_stays_listed_until_it_is_forgotten_and_reconnects_when_it_returns()
    {
        var (manager, dus, _, _, plugged) = Make("""{"modules":{"SERIAL":{"display":"pfd","label":"DU1"}}}""");
        using var _m = manager;
        manager.ScanOnce();
        Assert.False(manager.Forget(Serial));                       // connected: forgetting it would be pointless
        dus[0].Unplug();
        Until(() => manager.Connection(Serial)?.Alive == false);
        plugged.Clear();
        manager.ScanOnce();
        Assert.False(manager.Status().Single().Alive);              // still listed: a missing DU must be visible

        plugged.Add(Path);
        manager.ScanOnce();
        Assert.Equal(2, dus.Count);
        Assert.True(manager.Status().Single().Alive);

        dus[1].Unplug();
        Until(() => manager.Connection(Serial)?.Alive == false);
        plugged.Clear();
        manager.ScanOnce();
        Assert.True(manager.Forget(Serial));
        Assert.Empty(manager.Status());
    }
}
