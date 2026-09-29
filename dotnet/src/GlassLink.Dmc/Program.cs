// GlassLink DMC for .NET.
//
//   GlassLink [--config <config.json>] [--port 8765] [--no-tray] [--with-sim] [--quit]
//
// Without --config the configuration is config.json in or above the working directory (a checkout), else
// %LOCALAPPDATA%\GlassLink\config.json (an installed copy), created from config.example.json on first start.
// --quit stops the DMC that is running (as the tray's Quit does) and returns when it has gone: for installers.
// --add-sim-entry / --remove-sim-entry put GlassLink into or take it out of the sim's exe.xml ("Start and stop with the
// simulator"): for the installer's task and the uninstaller.
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

        // The installer's "start with the simulator" task and the uninstaller: GlassLink into or out of the sim's exe.xml
        // (#46, #73). Exit code 2: the sim has not been started on this PC yet, so it has no exe.xml (the tray can add
        // the entry later).
        if (args.Contains("--remove-sim-entry") || args.Contains("--add-sim-entry"))
        {
            var add = args.Contains("--add-sim-entry");
            try
            {
                if (SimLaunch.Files.Count == 0)
                {
                    return add ? 2 : 0;
                }

                SimLaunch.Set(add, Option("--config") ?? DefaultConfigPath());
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Xml.XmlException or UnauthorizedAccessException)
            {
                return 1;
            }

            return 0;
        }

        if (Option("--render-menu") is { } folder)            // development aid: the tray menu as pictures, light and dark
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Tray.RenderPreview(Path.Combine(folder, "menu-dark.png"), Palette.Dark);
            Tray.RenderPreview(Path.Combine(folder, "menu-light.png"), Palette.Light);
            return 0;
        }

        // A tray program has no console: an exception nobody catches is written to the log folder and shown (#44).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Crash(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception, fatal: true);

        var configPath = Option("--config") ?? FindUpwards("config.json") ?? DefaultConfigPath();
        // Owned = no DMC runs. A mutex left by a DMC that ended without releasing it (or by a status window of an
        // earlier second start) is abandoned, not owned: that also means nobody runs (#36).
        using var single = new Mutex(false, @"Local\GlassLink.DMC");
        bool first;
        try
        {
            first = single.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            first = true;
        }

        if (args.Contains("--quit"))
        {
            if (first)
            {
                single.ReleaseMutex();
                return 0;
            }

            return QuitRunning(RunningPort(configPath, Option("--port")), single);
        }

        if (!first)
        {
            if (args.Contains(SimLaunch.WithSimFlag))
            {
                return 0;                                    // the sim started us but the DMC is running already (e.g. with Windows): nothing to do
            }

            // Started a second time: the DMC is running already, so this is someone looking for it. Show its status page.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            if (StatusWindow.Available)
            {
                var runningUrl = $"http://localhost:{RunningPort(configPath, Option("--port"))}/";
                single.Dispose();                            // this window must not keep the DMC's mutex alive (#36)
                StatusWindow.RunAlone(runningUrl);
                return 0;
            }

            MessageBox.Show("The GlassLink DMC is already running (look for its icon in the notification area).", "GlassLink DMC");
            return 2;
        }

        EnsureDefaultConfig(configPath);                     // after the mutex: two first starts at sign-in do not race (#44)
        DmcRuntime dmcCreated;
        try
        {
            dmcCreated = new DmcRuntime(configPath);
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show($"The GlassLink DMC cannot read its configuration:\n\n{ex.Message}", "GlassLink DMC", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using var dmc = dmcCreated;
        _log = dmc.Log;
        if (dmc.Config.LoadedFromBackup)
        {
            dmc.Log($"{configPath} was empty or broken: the last good save (config.json.bak) is used");
        }

        var server = dmc.Config.Read(root => root["server"]?.DeepClone() as JsonObject);
        var host = server?["host"].Text() ?? "0.0.0.0";
        var port = int.TryParse(Option("--port"), out var p) ? p : (int)(server?["port"]?.AsDouble() ?? 8765);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();                    // the DMC has its own log; the web server stays quiet
        // "0.0.0.0" in the configuration means every interface: IPv6 too, so that "localhost" (::1 first on Windows)
        // connects at once instead of after a 2 s fallback to 127.0.0.1
        builder.WebHost.UseUrls(host is "0.0.0.0" or "::" or "*" ? $"http://*:{port}" : $"http://{host}:{port}");
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        var app = builder.Build();
        app.Use(RequestGuard.Middleware(                     // who may change what (#1): see RequestGuard
            () => dmc.Config.Read(root => root["server"]?["allow_lan_control"] is { } v && v.GetValueKind() == System.Text.Json.JsonValueKind.True), dmc.Log));
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        Api.Map(app, dmc, () => app.Lifetime.StopApplication());

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (IOException ex)
        {
            dmc.Log($"cannot listen on {host}:{port}: {ex.Message} (is the Python DMC running?)");
            if (!args.Contains("--no-tray"))
            {
                MessageBox.Show($"The GlassLink DMC cannot use port {port}: {ex.Message}\n\nIs the Python DMC (or another program) using it?",
                    "GlassLink DMC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

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
            using var simWatch = args.Contains(SimLaunch.WithSimFlag) ? new SimWatch(dmc.Log, () => app.Lifetime.StopApplication()) : null;
            // POST /shutdown, --quit and "stop with the simulator" stop the host on another thread; the message loop
            // (and the status window with it) must be ended on this one, or closing the WebView throws and the DMC
            // keeps running (#35).
            var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            app.Lifetime.ApplicationStopping.Register(() => ui.Post(_ => Application.Exit(), null));
            Application.Run();
        }

        app.StopAsync().GetAwaiter().GetResult();
        dmc.Dispose();                                       // captures, DUs and SimConnect closed in order ...
        dmc.Log("stopped");
        try
        {
            single.ReleaseMutex();                           // ... before the mutex goes: --quit and the installer wait for this
        }
        catch (ApplicationException)
        {
            // not owned any more: nothing to release
        }

        return 0;                                            // leaving the using blocks closes captures, DUs and SimConnect in order
    }

    private static Action<string>? _log;

    private static void Crash(Exception? ex, bool fatal)
    {
        var text = $"{(fatal ? "fatal" : "unhandled")} {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}";
        try
        {
            if (_log is { } log)
            {
                log(text);
            }
            else
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassLink");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // nowhere to write: the message box still tells
        }

        MessageBox.Show($"The GlassLink DMC hit an error{(fatal ? " and has to stop" : "")}:\n\n{ex?.Message}\n\nDetails are in the DMC log.",
            "GlassLink DMC", MessageBoxButtons.OK, fatal ? MessageBoxIcon.Error : MessageBoxIcon.Warning);
    }

    /// <summary>An installed copy keeps its configuration (and logs) in the user's profile, where it may write.</summary>
    private static string DefaultConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassLink", "config.json");

    /// <summary>The first start of an installed copy copies config.example.json from next to GlassLink.exe, so the six
    /// Airbus displays are there from the start.</summary>
    private static void EnsureDefaultConfig(string path)
    {
        var example = Path.Combine(AppContext.BaseDirectory, "config.example.json");
        if (path == DefaultConfigPath() && !File.Exists(path) && File.Exists(example))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(example, path);
        }
    }

    /// <summary>Asks the running DMC to stop (POST /shutdown, the same as the tray's Quit: captures and DUs are closed in
    /// order, never killed) and waits until it has gone. 0 when it has, 1 when it did not go within 20 s.</summary>
    private static int QuitRunning(int port, Mutex single)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var body = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");     // the request guard wants JSON
            http.PostAsync($"http://localhost:{port}/shutdown", body).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // not listening (a broken instance?): only the wait below can tell
        }

        try
        {
            if (!single.WaitOne(TimeSpan.FromSeconds(20)))     // the mutex is released when the other process ends
            {
                return 1;
            }

            single.ReleaseMutex();
        }
        catch (AbandonedMutexException)
        {
            // the other process ended while holding it: that is what was asked for
        }

        return 0;
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
