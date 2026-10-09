using System.Text.Json.Nodes;
using GlassLink.Capture.Windows;
using GlassLink.Sim;

namespace GlassLink.Core.Tests;

public class SimTests
{
    private static JsonObject Config(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void The_toliss_in_x_plane_is_known_by_its_folder_and_finds_its_pop_outs_by_title()
    {
        var profile = XPlaneProfiles.Select("Aircraft/ToLissA321_V1p7p2/a321.acf")!;
        Assert.Equal("ToLiss", profile.Key);
        Assert.Equal(["ecam_lower", "ecam_upper", "fo_nd", "fo_pfd", "nd", "pfd"], profile.Displays.Keys.Order());
        Assert.Null(XPlaneProfiles.Select("Aircraft/Laminar Research/Cessna 172 SP/Cessna_172SP.acf"));
        Assert.Null(XPlaneProfiles.Select(""));

        var rule = profile.Rule("pfd")!;
        Assert.Equal((15, true), (rule.Frame, rule.ToolWindow));
        var popout = new WindowInfo(1, "ToLiss Captain Left DU", "X-System", "X-Plane.exe", false, default, default, default);
        Assert.True(rule.Match.Matches(popout));
        Assert.False(profile.Rule("nd")!.Match.Matches(popout));                // another display's window
        Assert.False(rule.Match.Matches(popout with { Process = "FlightSimulator2024.exe" }));
        Assert.Null(profile.Rule("mcdu"));                                       // not a ToLiss display GlassLink knows
    }

    [Fact]
    public void A_parked_window_never_lands_on_a_screen()
    {
        Rect[] qhd = [new(0, 0, 2560, 1440)], uhd = [new(0, 0, 3840, 2160)], two = [new(-1920, 0, 0, 1080), new(0, 0, 3840, 2160)];
        Assert.Equal((2600, 0), WindowFinder.OffScreen(2600, 0, 770, 800, qhd));           // already off the 2560 screen: as configured
        Assert.Equal((3880, 0), WindowFinder.OffScreen(2600, 0, 770, 800, uhd));           // on the 3840 screen: right of it
        Assert.Equal((4680, 820), WindowFinder.OffScreen(3400, 820, 770, 800, uhd));       // keeps its distance to the others
        Assert.Equal((5000, 0), WindowFinder.OffScreen(5000, 0, 770, 800, uhd));
        Assert.Equal((3880, 0), WindowFinder.OffScreen(100, 0, 770, 800, two));            // a place on a screen: right of all screens
        Assert.Equal((2600, 0), WindowFinder.OffScreen(2600, 0, 770, 800, []));            // no monitors known: as configured
    }

    [Fact]
    public void A_title_regex_that_is_no_regular_expression_matches_nothing_and_says_why()
    {
        var bad = WindowMatch.From(Config("""{"title_regex":"GlassLink:(pfd"}"""));   // hand-edited config.json
        var window = new WindowInfo(1, "GlassLink:pfd", "AceApp", "FlightSimulator2024.exe", false, default, default, default);
        Assert.False(bad.Matches(window));                               // no exception on every look (review C11)
        Assert.Contains("title_regex", bad.Problem);
        var good = WindowMatch.From(Config("""{"title_regex":"^GlassLink:(pfd|nd)$"}"""));
        Assert.True(good.Matches(window));
        Assert.Null(good.Problem);
    }

    [Fact]
    public void The_aircraft_dimming_setting_keeps_its_last_answer_while_the_aircraft_writes_the_file()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("glasslink-dimming").FullName, "settings.xml");
        File.WriteAllText(file, "<Settings><HomeCockpitMode>true</HomeCockpitMode></Settings>");
        Assert.Equal("Home Cockpit Mode", BrightnessLink.DimmingOn(file, "HomeCockpitMode", null, "Home Cockpit Mode", null, out var busy));
        Assert.False(busy);
        using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))     // the aircraft is saving it
        {
            Assert.Equal("Home Cockpit Mode", BrightnessLink.DimmingOn(file, "HomeCockpitMode", null, "Home Cockpit Mode", "Home Cockpit Mode", out busy));
            Assert.True(busy);                                           // not "off" for 5 s, which dimmed twice (review C10)
        }

        File.WriteAllText(file, "<Settings><HomeCockpitMode>false</HomeCockpitMode></Settings>");
        Assert.Null(BrightnessLink.DimmingOn(file, "HomeCockpitMode", null, "Home Cockpit Mode", "Home Cockpit Mode", out busy));
        File.Delete(file);
        Assert.Null(BrightnessLink.DimmingOn(file, "HomeCockpitMode", null, "Home Cockpit Mode", "Home Cockpit Mode", out busy));   // no such file: off
        Assert.False(busy);
    }

    [Fact]
    public void A_view_that_cannot_be_seen_does_not_stop_the_pop_out()
    {
        // no window to look at (handle 0): nothing to judge by, so the pop-out goes on as before; only a view seen moving
        // for the whole wait stops the click (review C4, which needs the sim to show)
        using var sim = new SimConnectClient();
        var procedure = new PopoutProcedure(new GlassLink.Core.Config.ConfigFile(new JsonObject()), new SimCamera(sim), _ => { });
        Assert.True(procedure.WaitUntilStill(0, maxSeconds: 1));
    }

    [Fact]
    public void A_cropped_picture_is_what_lies_inside_the_frame()
    {
        const int w = 6, h = 5, stride = w * 4 + 8, crop = 1;        // a stride wider than the row, as a capture can have
        var source = new byte[stride * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                source[y * stride + x * 4] = (byte)(10 * y + x);             // blue channel = row and column
            }
        }

        var inside = new byte[(w - 2 * crop) * (h - 2 * crop) * 4];
        GlassLink.Capture.DisplayCapture.CopyInside(source, stride, crop, w - 2 * crop, h - 2 * crop, inside);
        Assert.Equal(11, inside[0]);                                     // row 1, column 1
        Assert.Equal(14, inside[3 * 4]);                                 // the last column inside: 4
        Assert.Equal(31, inside[2 * (w - 2) * 4]);                       // the last row inside: 3
    }

    [Fact]
    public void The_built_in_fenix_profile_knows_all_six_displays_and_their_views()
    {
        var profile = Profiles.Select(Config("{}"), "FenixA320 CFM SL")!;
        Assert.Equal("Fenix", profile.Key);
        Assert.Equal("pfd_sphere", profile.Detect);
        Assert.Equal(30, profile.Zoom);
        Assert.Equal(["ecam_lower", "ecam_upper", "fo_nd", "fo_pfd", "nd", "pfd"], profile.Points.Keys.Order());
        Assert.Equal("reset", profile.Points["pfd"].Camera.Key);
        Assert.Equal("view:1:4", profile.Points["fo_pfd"].Camera.Key);          // clicked from the copilot seat
        Assert.Equal("N_DISPLAY_BRIGHTNESS_FI", profile.Brightness["fo_nd"]);
        Assert.Null(Profiles.Select(Config("{}"), "Cessna 172"));
    }

    [Fact]
    public void The_built_in_fslabs_profile_has_its_own_fo_view_and_lets_the_aircraft_dim()
    {
        var profile = Profiles.Select(Config("{}"), "FSLabs A321-251NX - Sunclass (OY-VKA)")!;
        Assert.Equal("FSLabs", profile.Key);
        Assert.Equal(["ecam_lower", "ecam_upper", "fo_nd", "fo_pfd", "nd", "pfd"], profile.Points.Keys.Order());
        Assert.Equal("reset", profile.Points["fo_nd"].Camera.Key);                  // in view from the captain's seat
        Assert.Equal("view:2:5", profile.Points["fo_pfd"].Camera.Key);              // the FSLabs' First Officer view
        Assert.Equal("view:2:5", profile.CopilotCamera!.Key);                       // what Learn (FO seat) uses
        Assert.True(profile.Points.Values.All(p => p.Fits(16.0 / 9)));
        Assert.True(profile.DimmingAlways);                                         // its pop-outs follow the knobs themselves
        Assert.Empty(profile.Brightness);
        Assert.False(Profiles.Select(Config("{}"), "FenixA320 CFM SL")!.DimmingAlways);
    }

    [Fact]
    public void A_click_point_fits_only_the_screen_shape_it_was_made_on()
    {
        var config = Config("""{"popout":{"profiles":{"Fenix":{"points":{"pfd":{"xy":[0.40,0.81],"aspect":2.3889}}}}}}""");
        var fenix = Profiles.Select(config, "FenixA320 CFM SL")!;
        Assert.Equal(16.0 / 9, fenix.Points["nd"].Aspect!.Value, 3);              // built in: made on a 16:9 screen
        Assert.True(fenix.Points["nd"].Fits(2560 / 1440.0));                        // any 16:9 resolution
        Assert.True(fenix.Points["nd"].Fits(3840 / 2160.0));
        Assert.False(fenix.Points["nd"].Fits(3440 / 1440.0));                       // 21:9: would miss, so it is not used
        Assert.True(fenix.Points["pfd"].Fits(3440 / 1440.0));                       // learned on the 21:9 screen
        Assert.True(new ClickPoint(0.5, 0.5, CameraSpec.PilotReset).Fits(1.6));     // shape not known: used as before
        Assert.Equal(("16:9", "21:9", "16:10", "32:9", "1.90:1"),
            (ClickPoint.Shape(1920 / 1080.0), ClickPoint.Shape(3440 / 1440.0), ClickPoint.Shape(1.6), ClickPoint.Shape(5120 / 1440.0), ClickPoint.Shape(1.9)));
    }

    [Fact]
    public void Learned_points_in_the_configuration_win_and_custom_camera_points_do_not_count()
    {
        var config = Config("""
            {"popout":{"zoom":35,"profiles":{
              "Fenix":{"points":{"pfd":[0.5,0.6],"old":{"xy":[0.1,0.1],"camera":{"mode":"custom","slot":8}}}},
              "PMDG 737":{"points":{"pfd":{"xy":[0.4,0.7],"camera":{"mode":"view","type":1,"index":4}}}}}}}
            """);
        var fenix = Profiles.Select(config, "FenixA320 CFM SL")!;
        Assert.Equal((0.5, 0.6), (fenix.Points["pfd"].X, fenix.Points["pfd"].Y));
        Assert.True(fenix.Points.ContainsKey("nd"));                              // the built-in ones stay
        Assert.False(fenix.Points.ContainsKey("old"));
        var pmdg = Profiles.Select(config, "PMDG 737-800 KLM")!;
        Assert.Equal((35.0, "view:1:4"), (pmdg.Zoom, pmdg.Points["pfd"].Camera.Key));
    }

    [Fact]
    public void The_pfd_sphere_is_found_by_its_blue_sky_half()
    {
        const int w = 640, h = 360;
        var pixels = new byte[w * h * 4];
        void Fill(int x0, int y0, int x1, int y1, byte b, byte g, byte r)
        {
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    (pixels[(y * w + x) * 4], pixels[(y * w + x) * 4 + 1], pixels[(y * w + x) * 4 + 2], pixels[(y * w + x) * 4 + 3]) = (b, g, r, 255);
                }
            }
        }

        Fill(0, 0, w, h, 30, 30, 30);                        // a dark cockpit
        Assert.Null(PfdDetector.Find(pixels, w, h, w * 4));
        Fill(20, 300, 140, 352, 200, 110, 40);               // a blue tablet screen, larger, with no ground under it
        Assert.Null(PfdDetector.Find(pixels, w, h, w * 4));
        Fill(300, 200, 380, 240, 200, 110, 40);              // the sky half of the sphere: 80 x 40, saturated blue
        Fill(300, 240, 380, 280, 43, 90, 138);               // and the brown ground under it
        Fill(500, 100, 510, 200, 200, 110, 40);              // something blue but tall: not it
        Fill(100, 50, 140, 60, 250, 250, 250);               // white: not it
        var found = PfdDetector.Find(pixels, w, h, w * 4);
        Assert.NotNull(found);
        Assert.InRange(found.Value.X, 336, 344);             // centre of the blob
        Assert.InRange(found.Value.Y, 236, 242);             // its lower edge = the horizon
        Assert.InRange(found.Value.Width, 76, 84);
    }

    [Fact]
    public void A_sphere_too_small_for_the_pfd_is_the_standby_horizon()
    {
        const int w = 640, h = 360;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (b, g, r) = x is >= 400 and < 440 && y is >= 200 and < 240 ? y < 220 ? (200, 110, 40) : (43, 90, 138) : (30, 30, 30);
                (pixels[(y * w + x) * 4], pixels[(y * w + x) * 4 + 1], pixels[(y * w + x) * 4 + 2], pixels[(y * w + x) * 4 + 3]) = ((byte)b, (byte)g, (byte)r, 255);
            }
        }

        // PFD and ND points 160 px apart: the PFD's sky would be about 80 px wide, this one is 40 (a PFD showing ATT flags)
        Assert.Equal(53, PfdDetector.MinWidth(160));
        Assert.NotNull(PfdDetector.Find(pixels, w, h, w * 4));
        Assert.Null(PfdDetector.Find(pixels, w, h, w * 4, minWidth: PfdDetector.MinWidth(160)));
    }

    [Fact]
    public void Key_combinations_are_parsed_modifiers_first()
    {
        Assert.Equal(new ushort[] { 0x10, 0x70 }, Input.ParseCombo("Shift + F1"));
        Assert.Equal(new ushort[] { 0x11, 0x12, '9' }, Input.ParseCombo("ctrl+alt+9"));
        Assert.Equal(new ushort[] { 0x7B }, Input.ParseCombo("f12"));
        foreach (var bad in new[] { "", "shift", "shift+f1+f2", "win+x", "f99" })
        {
            Assert.Throws<FormatException>(() => Input.ParseCombo(bad));
        }
    }

    [Fact]
    public void Pop_out_settings_come_from_the_configuration_with_defaults()
    {
        var s = PopoutSettings.From(Config("""{"popout":{"auto":true,"retry_s":30,"camera_restore_key":"shift+f1"}}"""));
        Assert.Equal((10.0, 30.0, 2, "shift+f1", true), (s.GraceSeconds, s.RetrySeconds, s.MaxAttempts, s.RestoreKey, s.Auto));
        Assert.Null(PopoutSettings.From(Config("{}")).RestoreKey);
    }

    [Fact]
    public void Window_rules_and_the_client_crop()
    {
        var w = new WindowInfo(1, "GlassLink:pfd", "AceApp", "FlightSimulator2024.exe", false,
            new Rect(2593, 0, 3375, 829), new Rect(2602, 52, 3370, 820), new Rect(2600, 0, 3372, 822));
        Assert.True(WindowMatch.From(Config("""{"process":"flightsimulator2024.exe","class":"AceApp","title":"glasslink:PFD"}""")).Matches(w));
        Assert.False(WindowMatch.From(Config("""{"title_exact":"GlassLink:pf"}""")).Matches(w));
        Assert.True(WindowMatch.From(Config("""{"client_size":[768,768]}""")).Matches(w));
        Assert.Equal((2, 52, 768, 768), WindowFinder.ClientCrop(w, 772, 822));   // a capture of the DWM frame
        Assert.Equal((9, 52, 768, 768), WindowFinder.ClientCrop(w, 782, 829));   // a capture of the window rectangle
    }
}
