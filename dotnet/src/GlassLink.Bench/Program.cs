// Bench tool for the DU layer: talks to real DUs over WinUSB without the rest of the DMC.
//
//   GlassLink.Bench list
//   GlassLink.Bench stream [--seconds 20] [--fps 30] [--size 768x768] [--only <serial prefix>] [--brightness 0..100] [--knob 0..100]
//        a moving test picture to the DUs; --knob switches the brightness between 100 and that value every 50 ms meanwhile
//   GlassLink.Bench tiles --serial <prefix> --layout 0,0,640,640;640,0,640,640 [--cards] [--seconds 20] [--fps 30]
//        a layout of tiles on one DU (its HDMI mode must fit: see mode), then test pictures to every tile; --cards shows
//        the tiles as test cards instead
//   GlassLink.Bench mode <0..4> --serial <prefix>          set a DU's HDMI mode (0 768x768, 1 1024x768, 2 800x600, 3 1280x720, 4 1920x1080 at 30 Hz); it restarts
//   GlassLink.Bench ident [--seconds 5]                   show each DU's label on its panel
//   GlassLink.Bench manage [--seconds 20] [--config ../config.json]   the DU manager with the real assignments
//   GlassLink.Bench run [--seconds 30] [--config ../config.json] [--sim] [--close-all]
//        capture the configured windows and feed the DUs; --sim adds SimConnect, automatic pop-out and the brightness
//        link and works on the real config.json; --close-all closes the pop-outs first (a cold start without restarting the sim)
//   GlassLink.Bench sim [--seconds 6] [<variable>...]      what SimConnect says: aircraft, camera, fps, brightness knobs
//        (the Fenix's, or the named variables, e.g. L:VC_MIP_CPT_DU_PNL_PFD_BRT_Knob, for another aircraft);
//        --set <variable>=<value> writes one first (to find out what a variable does)
//   GlassLink.Bench popout <display> [<display>...]        close those pop-outs and pop them out again from .NET
//   GlassLink.Bench altclick <x> <y>                       Right-Alt + click at a screen point in the sim, as the pop-out does
//        (to answer a Learn without a hand on the mouse)
//   GlassLink.Bench update <image.bin> --serial <prefix>  install firmware on one DU over USB
//
//   stream and tiles also take --444 (JPEG without chroma subsampling, as a display with subsample_420 off) and --busy
//   (gradients and a screen of text, for frame sizes like a real PFD instead of the small line drawing)
//
// Stop the DMC first: a DU can only be opened by one program at a time.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using GlassLink.Capture;
using GlassLink.Capture.Windows;
using GlassLink.Core.Config;
using GlassLink.Core.Du;
using GlassLink.Core.Usb;
using GlassLink.Sim;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;   // 3.5, not 3,5
var command = args.FirstOrDefault() ?? "list";
Pattern.Subsample420 = !args.Contains("--444");
Pattern.Busy = args.Contains("--busy");
string? Text(string name) => Array.IndexOf(args, name) is var t and >= 0 && t + 1 < args.Length ? args[t + 1] : null;
int Option(string name, int fallback) =>
    Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;

if (command == "sim")
{
    using var sim = new SimConnectClient(log: Console.WriteLine);
    var title = sim.Text("TITLE");
    var state = sim.Number("CAMERA STATE", "Enum");
    var zoom = sim.Number("COCKPIT CAMERA ZOOM", "Percent");
    var viewType = sim.Number("CAMERA VIEW TYPE AND INDEX:0", "Enum");
    var viewIndex = sim.Number("CAMERA VIEW TYPE AND INDEX:1", "Enum");
    var named = args.Skip(1).Where((a, i) => !a.StartsWith("--") && args[i] is not ("--seconds" or "--set")).ToList();
    var set = Text("--set")?.Split('=', 2) is [var setName, var setValue] ? (Variable: sim.Number(setName), Value: double.Parse(setValue, CultureInfo.InvariantCulture)) : default;
    var knobs = (named.Count > 0 ? named : new[] { "CO", "CI", "ECAM_U", "ECAM_L", "FO", "FI" }.Select(k => $"L:N_DISPLAY_BRIGHTNESS_{k}")).Select(n => sim.Number(n)).ToList();
    sim.Start();
    for (var i = 0; i < Option("--seconds", 6); i++)
    {
        Thread.Sleep(1000);
        if (i == 0 && set.Variable is not null)
        {
            Console.WriteLine($"set {set.Variable.Name} = {set.Value}: {sim.Set(set.Variable, set.Value)}");
            Thread.Sleep(500);
        }

        Console.WriteLine($"connected {sim.Connected}  '{title.Text}'  camera state {state.Value} view {viewType.Value}/{viewIndex.Value} zoom {zoom.Value:0}  " +
                          $"sim {sim.SimFps:0.0} fps  brightness {string.Join(" ", knobs.Select(k => k.Value is { } v ? v.ToString("0.00") : "-"))}");
    }

    return 0;
}

if (command == "altclick")
{
    WindowFinder.SetDpiAware();
    if (args.Length < 3 || PopoutProcedure.SimMainWindow() is not { } simWindow)
    {
        Console.WriteLine("usage: altclick <x> <y> (with the sim running)");
        return 1;
    }

    Console.WriteLine(Input.RightAltClick(simWindow.Handle, int.Parse(args[1], CultureInfo.InvariantCulture), int.Parse(args[2], CultureInfo.InvariantCulture)) ? "clicked" : "the sim could not be brought to the front");
    return 0;
}

if (command == "popout")
{
    // The pop-out procedure on the real sim: closes the named displays' windows and lets .NET pop them out again.
    WindowFinder.SetDpiAware();
    var config = ConfigFile.Load(Text("--config") ?? "../config.json");
    using var sim = new SimConnectClient(log: Console.WriteLine);
    var camera = new SimCamera(sim);
    sim.Start();
    for (var i = 0; i < 50 && !camera.Ready; i++)
    {
        Thread.Sleep(100);
    }

    if (!camera.Ready || !camera.InCockpit || Profiles.Select(config.Root, camera.Title) is not { } profile)
    {
        Console.WriteLine($"not possible now: connected {sim.Connected}, in cockpit {camera.InCockpit}, aircraft '{camera.Title}'");
        return 1;
    }

    var names = args.Skip(1).TakeWhile(a => !a.StartsWith("--")).ToList();
    foreach (var name in names)
    {
        if (WindowFinder.Find(new WindowMatch(PopoutProcedure.SimProcess, null, null, PopoutProcedure.TitlePrefix + name, null, null)) is { } open)
        {
            Console.WriteLine($"{name}: closing 0x{open.Handle:x}");
            WindowFinder.Close(open.Handle);
        }
    }

    Thread.Sleep(1500);
    var watch = Stopwatch.StartNew();
    var done = new PopoutProcedure(config, camera, Console.WriteLine).Run(names, profile);
    Console.WriteLine($"popped out {done.Count} of {names.Count} in {watch.Elapsed.TotalSeconds:0.0} s: {string.Join(", ", done)}");
    return done.Count == names.Count ? 0 : 1;
}

if (command == "run")
{
    // A DMC in miniature: every display of the configuration is captured and the DUs get their assigned displays.
    WindowFinder.SetDpiAware();
    var withSim = args.Contains("--sim");
    var copy = Path.Combine(Path.GetTempPath(), "glasslink-bench-config.json");
    File.Copy(Text("--config") ?? "../config.json", copy, overwrite: true);
    var config = ConfigFile.Load(withSim ? Text("--config") ?? "../config.json" : copy);      // pop-outs are recorded in the real file
    var slots = new Dictionary<string, FrameSlot>();
    var captures = new List<DisplayCapture>();
    using var manager = DuManager.ForWinUsb(config, n => slots.GetValueOrDefault(n), Console.WriteLine);
    manager.BandFactory = (name, width, height, parts) => new BandComposer(name, width, height, parts);
    foreach (var (name, node) in config.Root["displays"] as System.Text.Json.Nodes.JsonObject ?? [])
    {
        slots[name] = new FrameSlot(name);
        captures.Add(new DisplayCapture(name, (System.Text.Json.Nodes.JsonObject)node!, config.Root["capture"] as System.Text.Json.Nodes.JsonObject,
            slots[name], () => manager.IsShown(name), Console.WriteLine));
    }

    SimConnectClient? sim = null;
    AutoPopout? auto = null;
    BrightnessLink? link = null;
    if (withSim)
    {
        if (args.Contains("--close-all"))
        {
            foreach (var w in WindowFinder.Enumerate().Where(w => w.Title.StartsWith(PopoutProcedure.TitlePrefix, StringComparison.Ordinal)))
            {
                WindowFinder.Close(w.Handle);
            }

            Console.WriteLine("closed all GlassLink pop-outs");
        }

        sim = new SimConnectClient(log: Console.WriteLine);
        var camera = new SimCamera(sim);
        link = new BrightnessLink(config, sim, camera);
        manager.SimBrightness = link.For;
        sim.Start();
        auto = new AutoPopout(config, camera, () => captures.Where(c => !c.HasWindow).Select(c => c.Name).ToList(), Console.WriteLine);
    }

    manager.Start();
    var me = Process.GetCurrentProcess();
    var clock = Stopwatch.StartNew();
    var before = captures.ToDictionary(c => c.Name, c => c.Counters);
    var cpuBefore = me.TotalProcessorTime;
    while (clock.Elapsed.TotalSeconds < Option("--seconds", 30))
    {
        Thread.Sleep(5000);
        me.Refresh();
        Console.WriteLine($"-- {clock.Elapsed.TotalSeconds:0} s   DMC {(me.TotalProcessorTime - cpuBefore).TotalSeconds / 5 * 100:0.0} % of one core, {me.WorkingSet64 / 1e6:0} MB");
        cpuBefore = me.TotalProcessorTime;
        foreach (var c in captures)
        {
            var (now, was) = (c.Counters, before[c.Name]);
            before[c.Name] = now;
            Console.WriteLine($"  {c.Name,-12} {(c.Window is null ? c.Error : $"{c.Window.Client.Width}x{c.Window.Client.Height}"),-18} arrived {(now.Received - was.Received) / 5.0,5:0.0}/s  " +
                              $"changed {(now.Published - was.Published) / 5.0,5:0.0}/s  skipped {(now.Skipped - was.Skipped) / 5.0,5:0.0}/s  encode {now.EncodeMs,4:0.0} ms  {now.JpegBytes / 1024} KB");
        }

        if (withSim)
        {
            var b = link!.Status();
            Console.WriteLine($"  sim {(sim!.Connected ? $"'{b.Aircraft}' {sim.SimFps:0} fps" : "not connected")}   auto pop-out: {auto!.State.Status} - {auto.State.Detail}" +
                              $"{(b.Standdown is null ? "" : $"   brightness stands down: {b.Standdown}")}");
        }

        foreach (var du in manager.Status().Where(d => d.Alive))
        {
            Console.WriteLine($"  {du.Label,-12} -> {du.Display,-10} brightness {du.BrightnessSent,3} % (knob {du.BrightnessSim:0.00})  shows {du.Stats?.Fps,5:0.0} fps  decode {du.Stats?.DecodeMs:0.0}  draw {du.Stats?.DrawMs:0.0}  transfer {du.Stats?.RxMs:0.0} ms  {(du.Health.Count > 0 ? string.Join("; ", du.Health) : "ok")}");
        }
    }

    if (Text("--dump") is { } folder)                       // the last picture of every display, to look at the crop
    {
        Directory.CreateDirectory(folder);
        foreach (var (name, slot) in slots)
        {
            if (slot.Latest is { } last)
            {
                File.WriteAllBytes(Path.Combine(folder, name + ".jpg"), last.Jpeg.ToArray());
            }
        }
    }

    auto?.Dispose();
    captures.ForEach(c => c.Dispose());
    sim?.Dispose();
    return 0;
}

if (command == "manage")
{
    // The manager as the DMC will use it: hot-plug, assignments and trim from config.json (a copy: nothing is written back).
    var copy = Path.Combine(Path.GetTempPath(), "glasslink-bench-config.json");
    File.Copy(Text("--config") ?? "../config.json", copy, overwrite: true);
    var config = ConfigFile.Load(copy);
    var names = config.Read(root => (root["displays"] as System.Text.Json.Nodes.JsonObject)?.Select(kv => kv.Key).ToList() ?? []);
    var displays = names.ToDictionary(n => n, n => new FrameSlot(n));
    var pictures = names.Select((n, i) => Pattern.Render(768, 768, 60, i, n)).ToList();
    using var manager = DuManager.ForWinUsb(config, n => displays.GetValueOrDefault(n), Console.WriteLine);
    manager.BandFactory = (name, width, height, parts) => new BandComposer(name, width, height, parts);
    manager.Start();
    var clock = Stopwatch.StartNew();
    for (var f = 0; clock.Elapsed.TotalSeconds < Option("--seconds", 20); f++)
    {
        for (var i = 0; i < names.Count; i++)
        {
            displays[names[i]].Publish(pictures[i][f % 60]);
        }

        Thread.Sleep(33);
        if (f % 150 == 149)
        {
            foreach (var du in manager.Status())
            {
                Console.WriteLine($"  {du.Serial[..8]} '{du.Label}' -> {(du.Display.Length > 0 ? du.Display : "(not assigned)")}  alive {du.Alive}  " +
                                  $"fw {du.Info?.Firmware}  shows {du.Stats?.Fps:0.0} fps (decode {du.Stats?.DecodeMs:0.0} draw {du.Stats?.DrawMs:0.0} transfer {du.Stats?.RxMs:0.0} ms, sent {du.FramesSent})  brightness {du.BrightnessSent} % (trim {du.Trim})  {(du.Health.Count > 0 ? string.Join("; ", du.Health) : "ok")}");
            }
        }
    }

    return 0;
}

var paths = WinUsbTransport.FindDevicePaths();
if (paths.Count == 0)
{
    Console.WriteLine("no DU found (plugged in? is the DMC still running and holding them?)");
    return 1;
}

var connections = new List<DuConnection>();
foreach (var path in paths)
{
    try
    {
        var conn = new DuConnection(new WinUsbTransport(path), Console.WriteLine);
        conn.Start();
        connections.Add(conn);
    }
    catch (IOException ex)
    {
        Console.WriteLine(ex.Message);
    }
}

if (connections.Count == 0)
{
    return 1;
}

var deadline = Environment.TickCount64 + 4000;
while (connections.Any(c => c.Info is null) && Environment.TickCount64 < deadline)
{
    Thread.Sleep(50);
}

foreach (var c in connections)
{
    c.Ping();
}

Thread.Sleep(300);
foreach (var c in connections)
{
    Console.WriteLine(c.Info is { } i
        ? $"{c.Serial}  fw {i.Firmware} ({i.Build})  {i.Hardware}  {i.PanelWidth}x{i.PanelHeight}  {i.Slot}  up {i.UptimeSeconds} s  ping {c.PingMs:0.0} ms"
        : $"{c.Serial}  no INFO answer");
}

switch (command)
{
    case "ident":
        var n = 1;
        foreach (var c in connections)
        {
            c.Ident($"DU{n++}", Option("--seconds", 5));
        }

        Thread.Sleep(Option("--seconds", 5) * 1000);
        break;

    case "update":
        var target = connections.FirstOrDefault(c => Text("--serial") is { } prefix && c.Serial.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (target is null || args.Length < 2 || !File.Exists(args[1]))
        {
            Console.WriteLine("usage: update <image.bin> --serial <prefix>");
            break;
        }

        var watch = Stopwatch.StartNew();
        target.BeginUpdate(File.ReadAllBytes(args[1]));
        var shown = "";
        while (target.Ota.State == OtaState.Running && watch.Elapsed.TotalSeconds < 120)
        {
            if (target.Ota.Message != shown)
            {
                Console.WriteLine($"  {target.Ota.Progress,4:P0}  {shown = target.Ota.Message}");
            }

            Thread.Sleep(200);
        }

        Console.WriteLine($"  {target.Ota.State}: {target.Ota.Message} ({watch.Elapsed.TotalSeconds:0.0} s)");
        break;

    case "stream":
    {
        var size = (Text("--size") ?? "768x768").Split('x');
        var only = Text("--only");
        var targets = connections.Where(c => only is null || c.Serial.StartsWith(only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (Option("--brightness", -1) is var bright and >= 0)
        {
            targets.ForEach(c => c.SetBrightness(bright));         // below 100 the DU dims every picture: part of what is measured
        }

        // --knob <n>: the brightness goes between 100 and n every 50 ms while pictures stream, as a cockpit knob turned
        // during decodes (a change in the middle of one switched the DU's free dimming off for good, review 9 Oct 2026)
        using var knob = Option("--knob", -1) is var low and >= 0
            ? new Timer(_ => targets.ForEach(c => c.SetBrightness(Environment.TickCount64 / 50 % 2 == 0 ? 100 : low)), null, 0, 50)
            : null;
        Stream(targets, Option("--seconds", 20), Option("--fps", 30), int.Parse(size[0]), int.Parse(size[1]));
        break;
    }

    case "tiles":
    {
        var prefix = Text("--serial") ?? throw new InvalidOperationException("--serial <prefix> is needed");
        var du = connections.First(c => c.Serial.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var layout = (Text("--layout") ?? "0,0,640,640;640,0,640,640").Split(';')
            .Select(t => t.Split(',').Select(int.Parse).ToArray()).Select(r => new DuConnection.Tile(r[0], r[1], r[2], r[3])).ToList();
        Thread.Sleep(1500);                                   // INFO first (caps)
        Console.WriteLine($"{du.Short}: tiles supported: {du.SupportsTiles}, panel {du.Info?.PanelWidth}x{du.Info?.PanelHeight}");
        if (Option("--brightness", -1) is var tb and >= 0)
        {
            du.SetBrightness(tb);
        }

        du.SetLayout(layout, args.Contains("--cards"));
        if (args.Contains("--cards"))
        {
            Console.WriteLine($"showing {layout.Count} test card(s) for {Option("--seconds", 20)} s");
            Thread.Sleep(Option("--seconds", 20) * 1000);
            break;
        }

        StreamTiles(du, layout, Option("--seconds", 20), Option("--fps", 30));
        break;
    }

    case "mode":
    {
        var prefix = Text("--serial") ?? throw new InvalidOperationException("--serial <prefix> is needed");
        var du = connections.First(c => c.Serial.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        du.SetMode(int.Parse(args[1]));
        Console.WriteLine($"{du.Short}: HDMI mode {args[1]} sent; the DU restarts");
        Thread.Sleep(1000);
        break;
    }
}

foreach (var c in connections)
{
    c.Source = null;
    c.Dispose();
}

return 0;

static void StreamTiles(DuConnection du, List<DuConnection.Tile> layout, int seconds, int fps)
{
    Console.WriteLine($"rendering {layout.Count} test pictures ({string.Join(", ", layout.Select(t => $"{t.Width}x{t.Height}"))}, {fps} fps each for {seconds} s)...");
    var slots = layout.Select((t, i) => new FrameSlot($"tile{i}")).ToArray();
    var cycles = layout.Select((t, i) => Pattern.Render(t.Width, t.Height, 60, i % 2, $"TILE {i + 1}")).ToList();
    du.TileSources = slots;
    var watch = Stopwatch.StartNew();
    var frame = 0;
    var lastReport = 0L;
    var sentAtReport = du.FramesSent;
    while (watch.Elapsed.TotalSeconds < seconds)
    {
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i].Publish(cycles[i][frame % cycles[i].Count]);
        }

        frame++;
        var wait = frame * 1000.0 / fps - watch.Elapsed.TotalMilliseconds;
        if (wait > 0)
        {
            Thread.Sleep((int)wait);
        }

        if (watch.ElapsedMilliseconds - lastReport >= 5000)
        {
            var dt = (watch.ElapsedMilliseconds - lastReport) / 1000.0;
            lastReport = watch.ElapsedMilliseconds;
            var rate = (du.FramesSent - sentAtReport) / dt;
            sentAtReport = du.FramesSent;
            var s = du.Stats;
            Console.WriteLine($"  {du.Short}  sent {rate,5:0.0} tiles/s ({rate / slots.Length:0.0} per tile)   DU shows {s?.Fps,5:0.0}/s  decode {s?.DecodeMs,4:0.0} ms  draw {s?.DrawMs,4:0.0} ms  " +
                              $"transfer {s?.RxMs,4:0.0} ms  dropped {s?.Dropped}  {(du.HealthReasons.Count > 0 ? string.Join("; ", du.HealthReasons) : "ok")}");
        }
    }

    du.TileSources = new IFrameSource?[slots.Length];
    du.SetLayout([]);
}

static void Stream(List<DuConnection> connections, int seconds, int fps, int width = 768, int height = 768)
{
    Console.WriteLine($"rendering test pictures ({width}x{height}, {fps} fps for {seconds} s to {connections.Count} DU(s))...");
    var slots = connections.Select((c, i) => new FrameSlot($"pattern{i}")).ToList();
    var cycles = slots.Select((_, i) => Pattern.Render(width, height, 60, i)).ToList();     // one second of motion each, encoded up front
    Console.WriteLine($"  {cycles[0].Average(f => f.Length) / 1024:0} KB per frame");
    for (var i = 0; i < connections.Count; i++)
    {
        connections[i].Source = slots[i];
    }

    var watch = Stopwatch.StartNew();
    var frame = 0;
    var lastReport = 0L;
    var sentAtReport = connections.Select(c => c.FramesSent).ToArray();
    while (watch.Elapsed.TotalSeconds < seconds)
    {
        for (var i = 0; i < slots.Count; i++)
        {
            slots[i].Publish(cycles[i][frame % cycles[i].Count]);
        }

        frame++;
        var next = frame * 1000.0 / fps;
        var wait = next - watch.Elapsed.TotalMilliseconds;
        if (wait > 0)
        {
            Thread.Sleep((int)wait);
        }

        if (watch.ElapsedMilliseconds - lastReport >= 5000)
        {
            var dt = (watch.ElapsedMilliseconds - lastReport) / 1000.0;
            lastReport = watch.ElapsedMilliseconds;
            for (var i = 0; i < connections.Count; i++)
            {
                var c = connections[i];
                var rate = (c.FramesSent - sentAtReport[i]) / dt;
                sentAtReport[i] = c.FramesSent;
                var s = c.Stats;
                Console.WriteLine($"  {c.Short}  sent {rate,5:0.0} fps   DU shows {s?.Fps,5:0.0} fps  decode {s?.DecodeMs,4:0.0} ms  draw {s?.DrawMs,4:0.0} ms  " +
                                  $"transfer {s?.RxMs,4:0.0} ms  dropped {s?.Dropped}  {(c.HealthReasons.Count > 0 ? string.Join("; ", c.HealthReasons) : "ok")}");
            }
        }
    }

    var process = Process.GetCurrentProcess();
    Console.WriteLine($"bench CPU {process.TotalProcessorTime.TotalSeconds / watch.Elapsed.TotalSeconds * 100:0.0} % of one core, " +
                      $"{process.WorkingSet64 / 1e6:0} MB");
}

internal static class Pattern
{
    public static bool Subsample420 = true;
    public static bool Busy;

    /// <summary>A sweep hand, a moving bar and a frame counter, as JPEGs: enough change to look like an instrument.
    /// Encoded like the DMC does (libjpeg-turbo, quality 85). --busy adds what makes a real PFD's frames large.</summary>
    public static List<byte[]> Render(int width, int height, int count, int variant, string? caption = null)
    {
        var frames = new List<byte[]>(count);
        using var encoder = new JpegEncoder(85, Subsample420);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        using var font = new Font("Consolas", 40, FontStyle.Bold);
        using var small = new Font("Consolas", Math.Max(8, height / 60f));
        using var pen = new Pen(variant == 0 ? Color.Lime : Color.Cyan, 6);
        using var sky = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, width, height), Color.DeepSkyBlue, Color.SaddleBrown, 90f);
        var s = Math.Min(width, height) / 768f;              // the same picture on any screen size
        var (cx, cy) = (width / 2f, height / 2f);
        for (var i = 0; i < count; i++)
        {
            if (Busy)
            {
                g.FillRectangle(sky, 0, 0, width, height);
                for (var line = 0; line * small.Height < height; line++)
                {
                    g.DrawString($"ALT {(line * 137 + i * 7) % 39000:00000} SPD {(line * 31 + i) % 350:000} HDG {(line * 11 + i) % 360:000} QNH 1013 V/S {line * 100 - 900:+0000;-0000}",
                                 small, line % 3 == 0 ? Brushes.White : line % 3 == 1 ? Brushes.Lime : Brushes.Magenta, 4, line * small.Height);
                }
            }
            else
            {
                g.Clear(Color.Black);
            }

            g.DrawEllipse(Pens.White, cx - 300 * s, cy - 300 * s, 600 * s, 600 * s);
            var a = i * 2 * Math.PI / count;
            g.DrawLine(pen, cx, cy, cx + (float)(290 * s * Math.Sin(a)), cy - (float)(290 * s * Math.Cos(a)));
            g.FillRectangle(Brushes.Orange, i * (width - 60) / count, height - 48, 60, 24);
            g.DrawString($"{caption ?? $".NET DU{variant + 1}"}  {i:00}", font, Brushes.White, 20, 16);
            var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    frames.Add(encoder.Encode(new ReadOnlySpan<byte>((void*)bits.Scan0, bits.Stride * height), width, height, bits.Stride));
                }
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }
        }

        return frames;
    }
}
