using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GlassLink.Capture;
using GlassLink.Capture.Windows;
using GlassLink.Sim;

namespace GlassLink.Dmc;

/// <summary>
/// One file to attach to an issue (status page System tab, or the tray menu): what the status page shows, the
/// configuration, the logs of the last days and a few facts about the PC. The user's name, the computer's name, its
/// network names and addresses are blanked out; nothing is sent anywhere, the user decides what to share.
/// </summary>
public static partial class Diagnostics
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string FileName => $"glasslink-diagnostics-{DateTime.Now:yyyy-MM-dd-HHmm}.zip";

    /// <summary>The zip for this DMC.</summary>
    public static byte[] Build(DmcRuntime dmc)
    {
        var files = new List<(string Name, string Text)>
        {
            ("README.txt", Readme),
            ("status.json", Api.Status(dmc).ToJsonString(Indented)),
            ("config.json", dmc.Config.Snapshot().ToJsonString(Indented)),
            ("system.txt", SystemFacts(dmc)),
        };
        files.AddRange(RecentLogs(Path.Combine(dmc.Root, "logs"), 3).Select(f => ($"logs/{Path.GetFileName(f)}", Tail(f))));
        var crash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassLink", "crash.log");
        if (File.Exists(crash))
        {
            files.Add(("logs/crash.log", Tail(crash)));
        }

        return Zip(files, Redactor(Environment.UserName, Environment.MachineName, OwnAddresses(), DnsSuffixes()));
    }

    /// <summary>The entries as a zip, every text through <paramref name="redact"/> first.</summary>
    public static byte[] Zip(IEnumerable<(string Name, string Text)> files, Func<string, string> redact)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                writer.Write(redact(text));
            }
        }

        return memory.ToArray();
    }

    /// <summary>Blanks what says who or where the user is: the profile folder in paths (C:\Users\name, also in its
    /// short 8.3 form), the computer's name, its network domain names, IPv4 addresses (loopback and 0.0.0.0 stay) and
    /// the PC's own IPv6 addresses. DU serials and display names stay: they are what a helper needs.</summary>
    public static Func<string, string> Redactor(string user, string machine, IEnumerable<string> ownAddresses, IEnumerable<string> domains)
    {
        var addresses = ownAddresses.Where(a => a.Contains(':') && a.Length > 4).OrderByDescending(a => a.Length).ToList();
        var names = domains.Where(d => d.Length > 0).OrderByDescending(d => d.Length).ToList();
        return text =>
        {
            text = ProfilePath().Replace(text, m => m.Groups[1].Value + "<user>");
            foreach (var a in addresses)
            {
                text = text.Replace(a, "<address>", StringComparison.OrdinalIgnoreCase);
            }

            text = Ipv4().Replace(text, m => m.Value is "127.0.0.1" or "0.0.0.0" ? m.Value : "<address>");
            foreach (var d in names)
            {
                text = text.Replace(d, "<domain>", StringComparison.OrdinalIgnoreCase);
            }

            if (machine.Length > 0)
            {
                text = Regex.Replace(text, $@"\b{Regex.Escape(machine)}\b", "<pc>", RegexOptions.IgnoreCase);
            }

            return user.Length > 2 ? Regex.Replace(text, $@"\b{Regex.Escape(user)}\b", "<user>", RegexOptions.IgnoreCase) : text;
        };
    }

    [GeneratedRegex(@"(?i)\b([a-z]:[\\/]+users[\\/]+)[^\\/""'\s]+")]
    private static partial Regex ProfilePath();

    [GeneratedRegex(@"\b(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}\b")]
    private static partial Regex Ipv4();

    private const string Readme = """
        GlassLink diagnostics

        status.json   what the status page showed when this was saved (displays, DUs, sim, advice)
        config.json   the DMC's configuration
        system.txt    Windows, .NET, the graphics adapters, the screens, how the DMC runs
        logs/         the DMC's logs of the last three days (and crash.log if there is one)

        The user's name, the computer's name, network names and addresses are replaced with <user>, <pc>, <domain> and
        <address>. Read it before you share it: nothing here was sent anywhere.
        """;

    private static string SystemFacts(DmcRuntime dmc)
    {
        var s = new StringBuilder();
        s.AppendLine($"GlassLink DMC {dmc.Version} ({dmc.Build}), firmware offered {dmc.FirmwareVersion}, {(DmcRuntime.Installed ? "installed" : "run from a checkout")}");
        s.AppendLine($"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        s.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        s.AppendLine($"Logical CPUs: {Environment.ProcessorCount}; DMC priority {dmc.Process.Priority}, CPUs [{string.Join(",", dmc.Process.Affinity)}]");
        using (var me = System.Diagnostics.Process.GetCurrentProcess())
        {
            s.AppendLine($"DMC: up {DateTime.UtcNow - dmc.Started:d\\.hh\\:mm\\:ss}, {me.WorkingSet64 / (1024 * 1024)} MB, {me.TotalProcessorTime.TotalSeconds:0} s CPU");
        }

        foreach (var adapter in GraphicsAdapters.Describe())
        {
            s.AppendLine($"Graphics: {adapter}");
        }

        foreach (var m in WindowFinder.Monitors())
        {
            s.AppendLine($"Screen: {m.Width}x{m.Height} at {m.Left},{m.Top}");
        }

        s.AppendLine($"SimConnect.dll: {(SimConnectClient.LibraryFound ? "found" : "not found")}; MSFS connected: {dmc.Sim.Connected}; X-Plane: {dmc.XPlane}");
        s.AppendLine($"Status window (WebView2): {(StatusWindow.Available ? "available" : "not available")}");
        return s.ToString();
    }

    private static IEnumerable<string> RecentLogs(string folder, int count) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "dmc-*.log").OrderDescending().Take(count).Order() : [];

    /// <summary>The end of a log (at most 2 MB), read while the DMC may be writing to it.</summary>
    private static string Tail(string file, int max = 2 * 1024 * 1024)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > max)
        {
            stream.Seek(-max, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Each address as Windows writes it, and a link-local one also without its zone (fe80::1%12 and fe80::1).</summary>
    private static IEnumerable<string> OwnAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address.ToString())
            .SelectMany(a => a.IndexOf('%') is var zone and > 0 ? new[] { a, a[..zone] } : [a]);

    private static IEnumerable<string> DnsSuffixes() =>
        NetworkInterface.GetAllNetworkInterfaces().Select(n => n.GetIPProperties().DnsSuffix).Append(IPGlobalProperties.GetIPGlobalProperties().DomainName);
}
