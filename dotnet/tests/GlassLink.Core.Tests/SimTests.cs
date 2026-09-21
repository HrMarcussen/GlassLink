using System.Text.Json.Nodes;
using GlassLink.Capture.Windows;
using GlassLink.Sim;

namespace GlassLink.Core.Tests;

public class SimTests
{
    private static JsonObject Config(string json) => (JsonObject)JsonNode.Parse(json)!;

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
        Fill(300, 200, 380, 240, 200, 110, 40);              // the sky half of the sphere: 80 x 40, saturated blue
        Fill(500, 100, 510, 200, 200, 110, 40);              // something blue but tall: not it
        Fill(100, 50, 140, 60, 250, 250, 250);               // white: not it
        var found = PfdDetector.Find(pixels, w, h, w * 4);
        Assert.NotNull(found);
        Assert.InRange(found.Value.X, 336, 344);             // centre of the blob
        Assert.InRange(found.Value.Y, 236, 242);             // its lower edge = the horizon
        Assert.InRange(found.Value.Width, 76, 84);
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
