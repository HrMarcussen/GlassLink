using System.Text.Json.Nodes;
using GlassLink.Core.Config;
using GlassLink.Core.Du;
using GlassLink.Dmc;

namespace GlassLink.Core.Tests;

public class AdvisorTests
{
    private long _now = 1_000_000;

    private Advisor Make(GpuFacts? gpu = null, SimFacts? sim = null) =>
        new(() => gpu ?? new GpuFacts([], false, false, false, false, false), () => sim ?? new SimFacts(null, null), () => _now);

    private static AdvisorInput Input(long received, bool inUse = true, bool hasWindow = true, double cap = 40, bool simDisplay = true) =>
        new([("pfd", received, inUse, hasWindow, cap, simDisplay)], [], 0, "idle", "", [], false, false, null, "");

    private IReadOnlyList<Advice> Run(Advisor advisor, double fps, int seconds, Func<long, AdvisorInput> input, ref long received)
    {
        IReadOnlyList<Advice> last = [];
        for (var i = 0; i < seconds / 2; i++)
        {
            _now += 2000;
            received += (long)(fps * 2);
            last = advisor.Advise(input(received));
        }

        return last;
    }

    [Fact]
    public void A_throttled_display_in_use_is_reported_after_a_while_not_at_once()
    {
        var advisor = Make(new GpuFacts(["AMD Radeon RX 7900 XTX"], true, false, true, false, false), new SimFacts(1, "FSRFG"));
        long received = 0;
        Assert.Empty(Run(advisor, 13, 6, r => Input(r), ref received));                       // too early
        var found = Run(advisor, 13, 10, r => Input(r), ref received);
        Assert.Equal(["slow_source"], found.Select(a => a.Id));
        Assert.StartsWith("AMD Fluid Motion Frames is switched ON", found[0].Steps[0]);        // the proven cause comes first
        Assert.Contains(found[0].Steps, s => s.Contains("Glass cockpit refresh rate is Medium."));
        Assert.DoesNotContain(found[0].Steps, s => s.Contains("NVIDIA"));
        Assert.NotEmpty(Run(advisor, 30, 4, r => Input(r), ref received));                     // back to 30: stays a little, no flicker
        Assert.Empty(Run(advisor, 30, 6, r => Input(r), ref received));                        // and goes away once it has stayed back 8 s
    }

    [Fact]
    public void Only_windows_of_the_sim_in_use_with_a_window_can_be_a_slow_source()
    {
        long a = 0, b = 0, c = 0, d = 0;
        Assert.Empty(Run(Make(), 1, 20, r => Input(r, simDisplay: false), ref a));             // a test pattern may be slow
        Assert.Empty(Run(Make(), 1, 20, r => Input(r, inUse: false, cap: 1), ref b));          // the preview rate of an unused display
        Assert.Empty(Run(Make(), 0, 20, r => Input(r, hasWindow: false), ref c));              // no window: another rule's business
        Assert.Empty(Run(Make(), 30, 20, r => Input(r), ref d));
    }

    [Fact]
    public void Steps_follow_the_graphics_card()
    {
        var nvidia = Advisor.SlowSourceSteps(new GpuFacts([], false, true, false, false, false), new SimFacts(2, null));
        Assert.Contains(nvidia, s => s.Contains("Smooth Motion"));
        Assert.DoesNotContain(nvidia, s => s.Contains("Glass cockpit refresh rate is"));       // already High
        Assert.Contains("check that the preset is Default", Advisor.SlowSourceSteps(new GpuFacts([], true, false, false, false, false), new SimFacts(null, null))[0]);
        Assert.Contains(Advisor.SlowSourceSteps(new GpuFacts([], false, false, false, false, false), new SimFacts(null, null)), s => s.Contains("driver-level frame generation"));
        Assert.Contains(Advisor.SlowSourceSteps(new GpuFacts([], true, false, false, true, false), new SimFacts(null, null)), s => s.Contains("frame rate limit is active"));
    }

    [Fact]
    public void Du_and_pop_out_rules()
    {
        var input = new AdvisorInput([],
        [
            ("aa11bb22cc", "DU1", true, "pfd", true, ["transfer 22 ms"]),
            ("dd44ee55ff", "", true, "", false, []),
            ("0011223344", "", false, "", true, []),
        ], 1, "gave_up", "", ["fo_nd"], false, false, null, "");
        var advice = Make().Advise(input);
        Assert.Equal(["du_behind:aa11bb22cc", "fw:aa11bb22cc", "unassigned:dd44ee55ff", "strays", "popout_gave_up"], advice.Select(a => a.Id));
        Assert.All(advice, a => Assert.True(a.Title.Length > 0 && a.Steps.Count > 0 && a.Level is "warn" or "info" && a.Tab.Length > 0));
    }

    [Fact]
    public void A_missing_simconnect_dll_is_said_in_words()
    {
        var input = new AdvisorInput([], [], 0, "waiting", GlassLink.Sim.SimConnectClient.LibraryMissing, [], false, false, null, "");
        var advice = Assert.Single(Make().Advise(input));
        Assert.Equal(("simconnect_missing", "system"), (advice.Id, advice.Tab));
        Assert.Contains(advice.Steps, s => s.Contains("SDK Installer"));
    }

    [Fact]
    public void A_du_that_cannot_keep_up_gets_its_own_advice_not_the_usb_steps()
    {
        var input = new AdvisorInput([],
        [
            ("aa11bb22cc", "MIP", true, "pfd, nd", false, ["shows 12 fps: its pictures come faster than it draws them"]),
            ("dd44ee55ff", "ECAM", true, "ecam_upper", false, ["shows 15 fps: its pictures come faster than it draws them", "transfer 22 ms"]),
        ], 0, "", "", [], false, false, null, "");
        var advice = Make().Advise(input);
        Assert.Equal(["du_slow:aa11bb22cc", "du_slow:dd44ee55ff", "du_behind:dd44ee55ff"], advice.Select(a => a.Id));
        Assert.Contains("fewer than 20", advice[0].Title);
        Assert.Equal("transfer 22 ms", advice[2].Detail);            // the USB advice keeps only what is about the link
    }

    [Fact]
    public void Sim_settings_are_read_from_usercfg()
    {
        var facts = Advisor.ParseSimFacts("{Graphics\n\tFrameGeneration FSRFG\n\t{GlassCockpitsRefreshRate\n\t\tQuality 1\n\t}\n}\n{GraphicsVR\n\t{GlassCockpitsRefreshRate\n\t\tQuality 2\n\t}\n}\n");
        Assert.Equal((1, "FSRFG"), (facts.GlassRefresh, facts.FrameGeneration));               // the desktop block, not VR
        Assert.Null(Advisor.ParseSimFacts("").GlassRefresh);
    }
}

public class RegistryAndFirmwareTests
{
    private static (DisplayRegistry Registry, ConfigFile Config, List<string> Removed) Make()
    {
        var config = new ConfigFile((JsonObject)JsonNode.Parse("""{"displays":{"pfd":{"match":{"title":"nothing matches this 7f3a"},"client_size":[768,768],"position":[2600,0]}}}""")!);
        var removed = new List<string>();
        var registry = new DisplayRegistry(config, _ => false, _ => { }) { Removed = removed.Add };
        return (registry, config, removed);
    }

    [Fact]
    public void Add_gets_a_title_a_size_and_a_free_parking_slot()
    {
        var (registry, _, _) = Make();
        using var r = registry;
        var added = registry.Add("  FO_PFD ", null);
        Assert.Equal("GlassLink:fo_pfd", added["match"]!["title_exact"]!.GetValue<string>());
        Assert.Equal("[3400,0]", added["position"]!.ToJsonString());                           // 2600,0 is taken by pfd
        Assert.Equal("[4200,0]", registry.Add("fo_nd", null)["position"]!.ToJsonString());
        // exact titles: the window of a display called "ecam_upper" is not also the window of one called "ecam"
        var ecam = GlassLink.Capture.Windows.WindowMatch.From(registry.Add("ecam", null)["match"] as JsonObject);
        Assert.False(ecam.Matches(new GlassLink.Capture.Windows.WindowInfo(1, "GlassLink:ecam_upper", "AceApp", "FlightSimulator2024.exe", false, default, default, default)));
        Assert.True(ecam.Matches(new GlassLink.Capture.Windows.WindowInfo(1, "GlassLink:ecam", "AceApp", "FlightSimulator2024.exe", false, default, default, default)));
        Assert.NotNull(registry.Slot("fo_pfd"));
        foreach (var bad in new[] { "", "1pfd", "FO PFD", "pfd!", new string('x', 30) })
        {
            Assert.Throws<DisplayException>(() => registry.Add(bad, null));
        }

        Assert.Throws<DisplayException>(() => registry.Add("pfd", null));
    }

    [Fact]
    public void After_a_fatal_error_the_captures_are_closed_but_never_waited_for_longer_than_the_limit()
    {
        Assert.True(DmcRuntime.Within(() => { }, TimeSpan.FromSeconds(2)));
        Assert.False(DmcRuntime.Within(() => throw new InvalidOperationException("broken"), TimeSpan.FromSeconds(2)));
        var started = Environment.TickCount64;
        using var never = new ManualResetEventSlim();
        Assert.False(DmcRuntime.Within(() => never.Wait(), TimeSpan.FromMilliseconds(200)));       // a lock the crashed thread holds
        Assert.InRange(Environment.TickCount64 - started, 150, 2000);
        never.Set();
    }

    [Fact]
    public void The_example_configuration_a_new_install_starts_from_finds_each_display_by_its_exact_title()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "config.example.json")))
        {
            folder = folder.Parent;
        }

        Assert.NotNull(folder);
        var example = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(folder.FullName, "config.example.json")))!;
        var displays = (JsonObject)example["displays"]!;
        Assert.Equal(6, displays.Count);
        foreach (var (name, node) in displays)
        {
            var match = GlassLink.Capture.Windows.WindowMatch.From(node!["match"] as JsonObject);
            Assert.Equal("GlassLink:" + name, match.TitleExact);
            Assert.Null(match.Title);                                                                // "GlassLink:pfd" is part of "GlassLink:pfd2"
            Assert.True(match.Matches(new GlassLink.Capture.Windows.WindowInfo(1, "GlassLink:" + name, "AceApp", "FlightSimulator2024.exe", false, default, default, default)));
        }

        Assert.Equal(8765, DmcRuntime.PortFrom(example["server"]));
    }

    [Fact]
    public void Update_validates_keeps_the_frame_slot_and_remove_tells_the_du_manager()
    {
        var (registry, config, removed) = Make();
        using var r = registry;
        registry.StartAll();
        var slot = registry.Slot("pfd");
        var updated = registry.Update("pfd", (JsonObject)JsonNode.Parse("""{"client_size":[800,800],"fps":"12","quality":null,"bogus":1}""")!);
        Assert.Equal(("[800,800]", 12.0), (updated["client_size"]!.ToJsonString(), updated["fps"]!.GetValue<double>()));
        var full = registry.Update("pfd", (JsonObject)JsonNode.Parse("""{"client_size":[1920,1080]}""")!);
        Assert.Equal("[1920,1072]", full["client_size"]!.ToJsonString());      // down to whole blocks: 1088 would not fit a 1080p DU (#62)
        Assert.False(updated.ContainsKey("bogus"));
        Assert.Same(slot, registry.Slot("pfd"));                                               // a DU showing it sees the frame numbers continue
        foreach (var bad in new[] { """{"fps":500}""", """{"client_size":[1,2]}""", """{"client_size":"wide"}""", """{"quality":5}""" })
        {
            Assert.Throws<DisplayException>(() => registry.Update("pfd", (JsonObject)JsonNode.Parse(bad)!));
        }

        registry.Remove("pfd");
        Assert.Equal(["pfd"], removed);
        Assert.Null(registry.Slot("pfd"));
        Assert.False(((JsonObject)config.Root["displays"]!).ContainsKey("pfd"));
        Assert.Throws<DisplayException>(() => registry.Remove("pfd"));
    }

    [Fact]
    public void Max_size_shrinks_a_picture_by_area_averaging_to_whole_16_pixel_blocks()
    {
        const int w = 64, h = 32;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                pixels[(y * w + x) * 4] = (byte)(x < 32 ? 0 : 200);      // blue: left half 0, right half 200
                pixels[(y * w + x) * 4 + 3] = 255;
            }
        }

        Assert.Null(GlassLink.Capture.Downscale.Fit(pixels, w, h, w * 4, 64));       // fits already
        Assert.Equal((48, 16), GlassLink.Capture.Downscale.Fit(pixels, w, h, w * 4, 56) is { } f ? (f.Width, f.Height) : default);   // never past max_size (#62)
        var (small, sw, sh) = GlassLink.Capture.Downscale.Fit(pixels, w, h, w * 4, 32)!.Value;
        Assert.Equal((32, 16), (sw, sh));
        Assert.Equal(0, small[0]);                                        // left
        Assert.Equal(200, small[31 * 4]);                                 // right
        Assert.Equal(255, small[3]);                                      // alpha kept
        var (_, ow, oh) = GlassLink.Capture.Downscale.Fit(pixels, w, h, w * 4, 40)!.Value;
        Assert.Equal((0, 0), (ow % 16, oh % 16));                         // the DU's decoder works in 16 x 16 blocks
    }

    [Fact]
    public void The_sim_launch_entry_is_added_and_removed_without_touching_the_others()
    {
        var file = Path.Combine(Path.GetTempPath(), $"glasslink-exe-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SimBase.Document Type=\"Launch\" version=\"1,0\">\n  <Descr>Launch</Descr>\n  <Launch.Addon>\n    <Name>FSUIPC7</Name>\n    <Disabled>False</Disabled>\n    <Path>C:\\FSUIPC7\\FSUIPC7.exe</Path>\n    <CommandLine>-auto</CommandLine>\n  </Launch.Addon>\n</SimBase.Document>");
        try
        {
            Assert.False(SimLaunch.IsEnabledIn(file));
            SimLaunch.Set(true, "C:\\Dev\\GlassLink\\config.json", file, "C:\\Program Files\\GlassLink\\GlassLink.exe");
            var text = File.ReadAllText(file);
            Assert.True(SimLaunch.IsEnabledIn(file));
            Assert.Contains("<Name>FSUIPC7</Name>", text);
            Assert.Contains("<CommandLine>-auto</CommandLine>", text);
            Assert.Contains("<Name>GlassLink DMC</Name>", text);
            Assert.Contains("--config \"C:\\Dev\\GlassLink\\config.json\" --with-sim", text);
            Assert.True(File.Exists(file + ".before-glasslink"));
            SimLaunch.Set(true, "C:\\Dev\\GlassLink\\config.json", file, "C:\\Program Files\\GlassLink\\GlassLink.exe");       // twice: still one entry
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "GlassLink DMC"));
            SimLaunch.Set(false, "C:\\Dev\\GlassLink\\config.json", file, "GlassLink.exe");
            text = File.ReadAllText(file);
            Assert.False(SimLaunch.IsEnabledIn(file));
            Assert.DoesNotContain("GlassLink", text);
            Assert.Contains("<Name>FSUIPC7</Name>", text);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".before-glasslink");
        }
    }

    [Fact]
    public void Firmware_versions_compare_by_number()
    {
        Assert.True(Firmware.IsOutdated("0.3.0", "0.5.0"));
        Assert.False(Firmware.IsOutdated("0.5.0", "0.5.0"));
        Assert.False(Firmware.IsOutdated("0.10.0", "0.5.0"));
        Assert.False(Firmware.IsOutdated("0.5.0-dirty", "0.5.0"));
        Assert.False(Firmware.IsOutdated(null, "0.5.0"));
        Assert.Equal([1, 0, 0], Firmware.VersionTuple("1.0.0-rc2"));               // a pre-release suffix is not part of the number
        Assert.True(Firmware.IsOutdated("1.0.0-rc2", "1.0.1"));
        Assert.Throws<InvalidDataException>(() => Firmware.Load(Path.Combine(Path.GetTempPath(), "no-such-image.bin")));
    }

    [Fact]
    public void A_signed_release_image_is_told_from_a_development_build_and_so_is_a_du_that_takes_only_signed_ones()
    {
        // an application image as the DMC checks it: header, descriptor with the project name
        var app = new byte[10_000];
        app[0] = 0xE9;
        BitConverter.GetBytes(0xABCD5432u).CopyTo(app, 32);
        System.Text.Encoding.ASCII.GetBytes("0.10.0").CopyTo(app, 32 + 16);
        System.Text.Encoding.ASCII.GetBytes(Firmware.ProjectName).CopyTo(app, 32 + 48);
        // espsecure sign_data --version 2: padded to whole 4 KB, then a sector that starts with magic 0xE7, version 2
        var signed = new byte[12_288 + 4096];
        app.CopyTo(signed, 0);
        (signed[12_288], signed[12_289]) = (0xE7, 0x02);

        var folder = Directory.CreateTempSubdirectory("glasslink-firmware").FullName;
        File.WriteAllBytes(Path.Combine(folder, "dev.bin"), app);
        File.WriteAllBytes(Path.Combine(folder, "release.bin"), signed);
        Assert.False(Firmware.Load(Path.Combine(folder, "dev.bin")).Signed);
        Assert.True(Firmware.Load(Path.Combine(folder, "release.bin")).Signed);
        Directory.Delete(folder, true);

        // INFO of a released build (sdkconfig.release) says it checks; older and development builds say nothing or 0
        DuInfo Info(string json) => DuInfo.From(System.Text.Json.JsonDocument.Parse(json).RootElement)!;
        Assert.True(Info("""{"fw":"0.10.0","signed":1}""").SignedUpdates);
        Assert.False(Info("""{"fw":"0.10.0","signed":0}""").SignedUpdates);
        Assert.False(Info("""{"fw":"0.9.0"}""").SignedUpdates);
    }
}
