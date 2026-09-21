// Bench tool for the DU layer: talks to real DUs over WinUSB without the rest of the DMC.
//
//   GlassLink.Bench list
//   GlassLink.Bench stream [--seconds 20] [--fps 30]      a moving test picture to every DU at once
//   GlassLink.Bench ident [--seconds 5]                   show each DU's label on its panel
//   GlassLink.Bench update <image.bin> --serial <prefix>  install firmware on one DU over USB
//
// Stop the Python DMC first: a DU can only be opened by one program at a time.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using GlassLink.Core.Du;
using GlassLink.Core.Usb;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;   // 3.5, not 3,5
var command = args.FirstOrDefault() ?? "list";
string? Text(string name) => Array.IndexOf(args, name) is var t and >= 0 && t + 1 < args.Length ? args[t + 1] : null;
int Option(string name, int fallback) =>
    Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;

var paths = WinUsbTransport.FindDevicePaths();
if (paths.Count == 0)
{
    Console.WriteLine("no DU found (plugged in? is the Python DMC still running and holding them?)");
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
        Stream(connections, Option("--seconds", 20), Option("--fps", 30));
        break;
}

foreach (var c in connections)
{
    c.Source = null;
    c.Dispose();
}

return 0;

static void Stream(List<DuConnection> connections, int seconds, int fps)
{
    Console.WriteLine($"rendering test pictures ({fps} fps for {seconds} s to {connections.Count} DU(s))...");
    var slots = connections.Select((c, i) => new FrameSlot($"pattern{i}")).ToList();
    var cycles = slots.Select((_, i) => Pattern.Render(768, 768, 60, i)).ToList();     // one second of motion each, encoded up front
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
    /// <summary>A sweep hand, a moving bar and a frame counter, as JPEGs: enough change to look like an instrument.</summary>
    public static List<byte[]> Render(int width, int height, int count, int variant)
    {
        var frames = new List<byte[]>(count);
        var codec = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        using var font = new Font("Consolas", 40, FontStyle.Bold);
        using var pen = new Pen(variant == 0 ? Color.Lime : Color.Cyan, 6);
        for (var i = 0; i < count; i++)
        {
            g.Clear(Color.Black);
            g.DrawEllipse(Pens.White, 84, 84, 600, 600);
            var a = i * 2 * Math.PI / count;
            g.DrawLine(pen, 384, 384, 384 + (float)(290 * Math.Sin(a)), 384 - (float)(290 * Math.Cos(a)));
            g.FillRectangle(Brushes.Orange, i * (width - 60) / count, 720, 60, 24);
            g.DrawString($".NET DU{variant + 1}  {i:00}", font, Brushes.White, 20, 16);
            using var stream = new MemoryStream();
            bitmap.Save(stream, codec, quality);
            frames.Add(stream.ToArray());
        }

        return frames;
    }
}
