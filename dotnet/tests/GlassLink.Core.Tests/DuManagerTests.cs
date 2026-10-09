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

    private static (DuManager Manager, List<FakeDu> Dus, Dictionary<string, FrameSlot> Displays, ConfigFile Config, List<string> Plugged) Make(string json, string? info = null)
    {
        var config = new ConfigFile((JsonObject)JsonNode.Parse(json.Replace("SERIAL", Serial))!);
        var displays = new Dictionary<string, FrameSlot> { ["pfd"] = new("pfd"), ["nd"] = new("nd") };
        var dus = new List<FakeDu>();
        var plugged = new List<string> { Path };
        var manager = new DuManager(config, name => displays.GetValueOrDefault(name), () => plugged.ToList(), _ =>
        {
            var du = new FakeDu { OpenedAfterPreviousClosed = dus.Count == 0 || dus[^1].Disposed };
            if (info is not null)
            {
                du.InfoJson = info;
            }

            dus.Add(du);
            return du;
        });
        return (manager, dus, displays, config, plugged);
    }

    [Fact]
    public void A_band_covers_the_rows_of_the_tiles_in_whole_blocks_and_stays_on_the_screen()
    {
        static DuConnection.Tile T(int x, int y, int w, int h) => new(x, y, w, h);
        Assert.Equal(T(0, 156, 1920, 768), DuManager.Band(1920, 1080, [T(40, 156, 768, 768), T(1100, 156, 768, 768)]));
        Assert.Equal(T(0, 100, 1920, 784), DuManager.Band(1920, 1080, [T(0, 101, 768, 768), T(900, 200, 640, 640)]));   // odd top: one row up, still whole blocks
        Assert.Equal(T(0, 296, 1920, 784), DuManager.Band(1920, 1080, [T(0, 300, 768, 768), T(900, 312, 768, 768)]));  // would end below the screen: moved up
        Assert.Null(DuManager.Band(1920, 1080, [T(0, 0, 768, 768), T(900, 312, 768, 768)]));   // 1080 rows are 1088 in whole blocks
        Assert.Null(DuManager.Band(1920, 1080, []));
    }

    [Fact]
    public void A_du_that_takes_a_band_gets_its_tiles_as_one_picture_but_test_cards_per_tile()
    {
        var (manager, dus, _, _, _) = Make("""{"modules":{"SERIAL":{"label":"MIP","screen":4,"tiles":{"pfd":{"x":40,"y":156},"nd":{"x":1100,"y":156}}}}}""",
            """{"fw":"0.6.0","panel":[1920,1080],"mode":4,"max_frame":1048576,"caps":["mode","tiles","band"]}""");
        using var _m = manager;
        manager.DisplaySize = _ => (768, 768);
        var made = new List<(string Name, int Width, int Height, IReadOnlyList<BandPart> Parts, FrameSlot Slot)>();
        manager.BandFactory = (name, width, height, parts) =>
        {
            var slot = new FrameSlot(name);
            lock (made)
            {
                made.Add((name, width, height, parts, slot));
            }

            return slot;
        };
        List<Message> Layouts() => dus[0].Of(MessageType.SetLayout).Where(m => m.Payload.Length > 0).ToList();
        manager.ScanOnce();
        Until(() => Layouts().Count == 1);

        var layout = Layouts()[0].Payload.ToArray();
        Assert.Equal(8, layout.Length);                              // one tile: the band
        Assert.Equal((0, 156, 1920, 768), (BitConverter.ToUInt16(layout, 0), BitConverter.ToUInt16(layout, 2), BitConverter.ToUInt16(layout, 4), BitConverter.ToUInt16(layout, 6)));
        var band = Assert.Single(made);
        Assert.Equal((1920, 768), (band.Width, band.Height));
        Assert.Equal([(40, 0, 768, 768), (1100, 0, 768, 768)], band.Parts.Select(p => (p.X, p.Y, p.Width, p.Height)));   // relative to the band
        Assert.Equal(["pfd", "nd"], band.Parts.Select(p => p.Source.Name));
        Assert.Equal(2, manager.Status().Single().Layout.Count);    // the status page still sees the user's two tiles

        band.Slot.Publish(new byte[] { 7 });
        Until(() => dus[0].Of(MessageType.Tile).Count == 1);
        Assert.Equal((0u, 7), (dus[0].Of(MessageType.Tile)[0].Arg, (int)dus[0].Of(MessageType.Tile)[0].Payload.Span[0]));

        manager.ShowCards(Serial, true);                             // the DU draws test cards per tile: the real tiles again
        Until(() => Layouts().Count == 2);
        Assert.Equal(16, Layouts()[1].Payload.Length);
        manager.ShowCards(Serial, false);
        Until(() => Layouts().Count == 3);
        Assert.Equal(8, Layouts()[2].Payload.Length);
        Assert.Equal(2, made.Count);                                 // a new band after the cards
    }

    [Fact]
    public void A_single_display_narrower_than_the_screen_goes_as_a_band_and_is_still_captured()
    {
        var (manager, dus, displays, _, _) = Make("""{"modules":{"SERIAL":{"label":"HDMI","screen":4,"display":"pfd"}}}""",
            """{"fw":"0.6.0","panel":[1920,1080],"mode":4,"max_frame":1048576,"caps":["mode","tiles","band"]}""");
        using var _m = manager;
        manager.DisplaySize = name => name == "pfd" ? (1056, 1056) : (1920, 1072);
        var made = new List<(int Width, int Height, IReadOnlyList<BandPart> Parts)>();
        manager.BandFactory = (name, width, height, parts) =>
        {
            lock (made)
            {
                made.Add((width, height, parts));
            }

            return new FrameSlot(name);
        };
        List<Message> Layouts() => dus[0].Of(MessageType.SetLayout).Where(m => m.Payload.Length > 0).ToList();
        manager.ScanOnce();
        Until(() => Layouts().Count == 1);

        var layout = Layouts()[0].Payload.ToArray();
        Assert.Equal((0, 12, 1920, 1056), (BitConverter.ToUInt16(layout, 0), BitConverter.ToUInt16(layout, 2), BitConverter.ToUInt16(layout, 4), BitConverter.ToUInt16(layout, 6)));
        var band = Assert.Single(made);
        Assert.Equal((432, 0, 1056, 1056), (band.Parts[0].X, band.Parts[0].Y, band.Parts[0].Width, band.Parts[0].Height));    // centred as the DU would
        Assert.True(manager.IsShown("pfd"));                         // inside a band is shown: its capture must keep running
        Assert.False(manager.IsShown("nd"));
        Assert.Empty(manager.Status().Single().Layout);              // no layout of the user's

        manager.Assign(Serial, display: "nd");                       // as wide as the screen: a plain FRAME, the DU draws it straight in
        Until(() => dus[0].Of(MessageType.SetLayout).Count > 0 && dus[0].Of(MessageType.SetLayout)[^1].Payload.Length == 0);
        displays["nd"].Publish(new byte[] { 5 });
        Until(() => dus[0].Of(MessageType.Frame).Count == 1);
        Assert.False(manager.IsShown("pfd"));
    }

    [Fact]
    public void A_du_with_tiles_gets_a_layout_and_each_tile_its_own_frames()
    {
        var (manager, dus, displays, config, _) = Make("""{"modules":{"SERIAL":{"label":"HDMI","screen":3,"tiles":{"pfd":{"x":0,"y":0},"nd":{"x":381,"y":0},"ecam_upper":{"x":0,"y":700}}}}}""");
        using var _m = manager;
        manager.DisplaySize = name => name == "pfd" ? (384, 384) : (384, 376);
        List<Message> Layouts() => dus[0].Of(MessageType.SetLayout).Where(m => m.Payload.Length > 0).ToList();
        manager.ScanOnce();
        Until(() => Layouts().Count == 1);                           // synced as soon as INFO arrives: the mode fits (3), so the layout goes

        var layout = Layouts()[0].Payload.ToArray();
        Assert.Equal(16, layout.Length);
        Assert.Equal((0, 0, 384, 384), (BitConverter.ToUInt16(layout, 0), BitConverter.ToUInt16(layout, 2), BitConverter.ToUInt16(layout, 4), BitConverter.ToUInt16(layout, 6)));
        Assert.Equal((381, 0, 384, 384), (BitConverter.ToUInt16(layout, 8), BitConverter.ToUInt16(layout, 10), BitConverter.ToUInt16(layout, 12), BitConverter.ToUInt16(layout, 14)));   // the position as given, the size up to whole 16-pixel blocks
        Assert.Contains("ecam_upper", manager.Status().Single().LayoutProblem);                     // not a display here: left out, reported
        Assert.Empty(dus[0].Of(MessageType.SetMode));
        var afterLayout = dus[0].All().SkipWhile(m => !(m.Type == MessageType.SetLayout && m.Payload.Length > 0)).ToList();
        Assert.DoesNotContain(afterLayout, m => m.Type == MessageType.SetAssigned && m.Arg == 0);  // no NOT ASSIGNED flash when switching to tiles

        displays["nd"].Publish(new byte[] { 2 });
        Until(() => dus[0].Of(MessageType.Tile).Count == 1);
        Assert.Equal((1u, 2), (dus[0].Of(MessageType.Tile)[0].Arg, (int)dus[0].Of(MessageType.Tile)[0].Payload.Span[0]));
        displays["pfd"].Publish(new byte[] { 1 });
        Until(() => dus[0].Of(MessageType.Tile).Count == 2);
        Assert.Equal(0u, dus[0].Of(MessageType.Tile)[1].Arg);
        Assert.Empty(dus[0].Of(MessageType.Frame));                  // never a plain frame in tile mode
        Assert.True(manager.IsShown("pfd") && manager.IsShown("nd"));

        manager.ShowCards(Serial, true);
        Until(() => Layouts().Count == 2);
        Assert.Equal(1u, Layouts()[1].Arg);
        var row = manager.Status().Single();
        Assert.True(row.Cards);
        Assert.Equal(2, row.Layout.Count);

        var before = dus[0].Of(MessageType.SetLayout).Count;
        manager.SetTiles(Serial, null);                              // a single-display DU again
        Until(() => dus[0].Of(MessageType.SetLayout).Count == before + 1);
        Assert.Equal(0, dus[0].Of(MessageType.SetLayout)[^1].Payload.Length);
        Assert.Null(((JsonObject)config.Root["modules"]![Serial]!)["tiles"]);
        Assert.Throws<ArgumentException>(() => manager.SetTiles(Serial, [.. Enumerable.Range(0, 7).Select(i => ("pfd", i * 16, 0))]));   // at most six
    }

    [Fact]
    public void A_du_that_falls_back_to_another_mode_is_asked_once_not_restarted_in_a_loop()
    {
        var (manager, dus, _, _, _) = Make("""{"modules":{"SERIAL":{"display":"pfd","screen":4}}}""");   // the fake DU reports mode 3
        using var _m = manager;
        manager.ScanOnce();
        Until(() => dus[0].Of(MessageType.SetMode).Count == 1);
        Assert.Equal(4u, dus[0].Of(MessageType.SetMode)[0].Arg);

        dus[0].Unplug();                                             // the DU restarts into the mode: the connection ends
        Until(() => manager.Connection(Serial)?.Alive == false);
        manager.ScanOnce();                                          // back, but in mode 3 again (the firmware fell back)
        Until(() => dus.Count == 2 && manager.Connection(Serial)?.Info is not null);
        manager.ScanOnce();
        Until(() => manager.Status().Single().LayoutProblem.Contains("stayed in mode 3"));
        Assert.Empty(dus[1].Of(MessageType.SetMode));                // not asked again: no restart loop

        manager.SetScreen(Serial, 4);                                // the user chooses again: one more try
        Until(() => dus[1].Of(MessageType.SetMode).Count == 1);
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
        Assert.True(manager.Forget(Serial.ToUpperInvariant()));     // as typed into a URL: serials are kept in lower case
        Assert.Empty(manager.Status());
    }

    [Fact]
    public void An_update_stays_on_the_page_after_the_du_restarted_and_the_old_connection_is_closed_before_the_new_one_opens()
    {
        var (manager, dus, _, _, _) = Make("""{"modules":{"SERIAL":{"display":"pfd","label":"DU1"}}}""");
        using var _m = manager;
        manager.ScanOnce();
        var image = new byte[40_000];
        Random.Shared.NextBytes(image);
        manager.Connection(Serial)!.BeginUpdate(image);
        Until(() => manager.Connection(Serial)!.Ota.State == OtaState.Ok, 5000);

        dus[0].Unplug();                                             // the DU restarts into the new firmware
        Until(() => manager.Connection(Serial)?.Alive == false);
        manager.ScanOnce();                                          // and is back
        Until(() => dus.Count == 2 && manager.Connection(Serial)?.Alive == true);
        Assert.True(dus[1].OpenedAfterPreviousClosed);               // the old handle went first (review L3)
        var ota = manager.Status().Single().Ota;
        Assert.Equal((OtaState.Ok, "installed, the DU restarted"), (ota.State, ota.Message));     // not lost with the connection (L9)
    }
}
