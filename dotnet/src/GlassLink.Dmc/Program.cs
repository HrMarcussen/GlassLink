// GlassLink DMC for .NET.
//
//   GlassLink [--config <config.json>] [--port 8765] [--no-tray]
//
// Same configuration file, USB protocol, HTTP API and status page as the Python DMC. Only one of the two can run at
// a time (they share the DUs and the port). Stop it from the tray menu, with POST /shutdown, or with
// `python tools/stop_server.py`.

using GlassLink.Core.Config;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GlassLink.Dmc;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        if (Option("--render-menu") is { } folder)            // development aid: the tray menu as pictures, light and dark
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Tray.RenderPreview(Path.Combine(folder, "menu-dark.png"), Palette.Dark);
            Tray.RenderPreview(Path.Combine(folder, "menu-light.png"), Palette.Light);
            return 0;
        }

        var configPath = Option("--config") ?? FindUpwards("config.json") ?? "config.json";
        using var single = new Mutex(true, @"Local\GlassLink.DMC", out var first);
        if (!first)
        {
            // Started a second time: the DMC is running already, so this is someone looking for it. Show its status page.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            if (StatusWindow.Available)
            {
                StatusWindow.RunAlone($"http://localhost:{RunningPort(configPath, Option("--port"))}/");
                return 0;
            }

            MessageBox.Show("The GlassLink DMC is already running (look for its icon in the notification area).", "GlassLink DMC");
            return 2;
        }

        using var dmc = new DmcRuntime(configPath);
        var server = dmc.Config.Read(root => root["server"]?.DeepClone() as JsonObject);
        var host = server?["host"]?.GetValue<string>() ?? "0.0.0.0";
        var port = int.TryParse(Option("--port"), out var p) ? p : (int)(server?["port"]?.AsDouble() ?? 8765);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();                    // the DMC has its own log; the web server stays quiet
        builder.WebHost.UseUrls($"http://{host}:{port}");
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        Api.Map(app, dmc, () => app.Lifetime.StopApplication());

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (IOException ex)
        {
            dmc.Log($"cannot listen on {host}:{port}: {ex.Message} (is the Python DMC running?)");
            return 1;
        }

        dmc.Start();
        var url = $"http://localhost:{port}/";
        dmc.Log($"status page: {url}");

        if (args.Contains("--no-tray"))
        {
            app.WaitForShutdown();
        }
        else
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            using var tray = new Tray(dmc, url, Application.Exit);
            app.Lifetime.ApplicationStopping.Register(Application.Exit);         // POST /shutdown ends the message loop too
            Application.Run();
        }

        app.StopAsync().GetAwaiter().GetResult();
        return 0;                                            // leaving the using blocks closes captures, DUs and SimConnect in order
    }

    /// <summary>The port of the DMC that is already running: the same answer it came to itself, without starting anything.</summary>
    private static int RunningPort(string configPath, string? option)
    {
        if (int.TryParse(option, out var port))
        {
            return port;
        }

        try
        {
            return (int)(JsonNode.Parse(File.ReadAllText(configPath))?["server"]?["port"]?.AsDouble() ?? 8765);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return 8765;
        }
    }

    private static string? FindUpwards(string file)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
            {
                var candidate = Path.Combine(folder.FullName, file);
                if (File.Exists(candidate) && File.Exists(Path.Combine(folder.FullName, "VERSION")))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
