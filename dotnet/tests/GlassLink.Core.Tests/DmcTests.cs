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
        Assert.Empty(Run(advisor, 30, 4, r => Input(r), ref received));                        // and it goes away when the rate is back
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
        Assert.Equal("GlassLink:fo_pfd", added["match"]!["title"]!.GetValue<string>());
        Assert.Equal("[3400,0]", added["position"]!.ToJsonString());                           // 2600,0 is taken by pfd
        Assert.Equal("[4200,0]", registry.Add("fo_nd", null)["position"]!.ToJsonString());
        Assert.NotNull(registry.Slot("fo_pfd"));
        foreach (var bad in new[] { "", "1pfd", "FO PFD", "pfd!", new string('x', 30) })
        {
            Assert.Throws<DisplayException>(() => registry.Add(bad, null));
        }

        Assert.Throws<DisplayException>(() => registry.Add("pfd", null));
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
    public void Firmware_versions_compare_by_number()
    {
        Assert.True(Firmware.IsOutdated("0.3.0", "0.5.0"));
        Assert.False(Firmware.IsOutdated("0.5.0", "0.5.0"));
        Assert.False(Firmware.IsOutdated("0.10.0", "0.5.0"));
        Assert.False(Firmware.IsOutdated("0.5.0-dirty", "0.5.0"));
        Assert.False(Firmware.IsOutdated(null, "0.5.0"));
        Assert.Throws<InvalidDataException>(() => Firmware.Load(Path.Combine(Path.GetTempPath(), "no-such-image.bin")));
    }
}
