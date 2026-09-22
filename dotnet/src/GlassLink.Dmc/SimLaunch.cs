using System.Xml.Linq;

namespace GlassLink.Dmc;

/// <summary>
/// "Start with the simulator": an entry in the sim's exe.xml, the list of programs MSFS starts with itself (the same
/// mechanism the Fenix, FSUIPC and GSX use). The entry starts GlassLink.exe with <c>--with-sim</c>, and a DMC started
/// that way quits by itself when the sim has gone (see <see cref="SimWatch"/>). The file is only ever changed on the
/// user's own click (tray menu), the first change leaves a copy next to it (exe.xml.before-glasslink), and other
/// entries are left exactly as they are.
/// </summary>
public static class SimLaunch
{
    public const string AddonName = "GlassLink DMC";
    public const string WithSimFlag = "--with-sim";

    /// <summary>The exe.xml of the sim installed on this PC: MSFS 2024 Store, MSFS 2024 Steam, then the 2020 editions;
    /// null if none is found (the sim writes it on first start).</summary>
    public static string? File_ => new[]
        {
            @"%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\exe.xml",
            @"%APPDATA%\Microsoft Flight Simulator 2024\exe.xml",
            @"%LOCALAPPDATA%\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\LocalCache\exe.xml",
            @"%APPDATA%\Microsoft Flight Simulator\exe.xml",
        }
        .Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists);

    public static bool Enabled
    {
        get
        {
            try
            {
                return File_ is { } file && Entry(XDocument.Load(file)) is { } e && !string.Equals(Value(e, "Disabled"), "true", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Adds or removes the entry. Throws with a plain message when the file cannot be changed.</summary>
    public static void Set(bool on, string configPath) => Set(on, configPath, File_ ?? throw new InvalidOperationException("the simulator's exe.xml was not found (has the simulator been started once on this PC?)"), Environment.ProcessPath ?? "GlassLink.exe");

    /// <summary>The same on any exe.xml (tests).</summary>
    public static void Set(bool on, string configPath, string file, string exe)
    {
        var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidOperationException($"{file} has no content");
        Entry(doc)?.Remove();
        if (on)
        {
            root.Add(new XElement("Launch.Addon",
                new XElement("Name", AddonName),
                new XElement("Disabled", "False"),
                new XElement("Path", exe),
                new XElement("CommandLine", $"--config \"{Path.GetFullPath(configPath)}\" {WithSimFlag}")));
        }

        var backup = file + ".before-glasslink";
        if (!File.Exists(backup))
        {
            File.Copy(file, backup);
        }

        var temp = file + ".glasslink-tmp";
        doc.Save(temp);
        File.Move(temp, file, overwrite: true);
    }

    public static bool IsEnabledIn(string file) => Entry(XDocument.Load(file)) is { } e && !string.Equals(Value(e, "Disabled"), "true", StringComparison.OrdinalIgnoreCase);

    private static XElement? Entry(XDocument doc) =>
        doc.Root?.Elements("Launch.Addon").FirstOrDefault(e => string.Equals(Value(e, "Name"), AddonName, StringComparison.OrdinalIgnoreCase));

    private static string? Value(XElement e, string name) => e.Element(name)?.Value.Trim();
}

/// <summary>
/// Ends the DMC when the simulator has gone: for a DMC the simulator started (<c>--with-sim</c>). The sim's process
/// is there from the moment it starts its add-ons, so "gone" is simply "no longer there", checked every few seconds
/// and confirmed twice so a hiccup in the process list does not end the DMC.
/// </summary>
public sealed class SimWatch : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly Action<string> _log;
    private readonly Action _quit;
    private int _missing;
    private bool _seen;

    public SimWatch(Action<string> log, Action quit)
    {
        (_log, _quit) = (log, quit);
        _timer = new System.Threading.Timer(_ => Tick(), null, 5000, 5000);
    }

    public void Dispose() => _timer.Dispose();

    private void Tick()
    {
        bool there;
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GlassLink.Sim.PopoutProcedure.SimProcess));
            there = processes.Length > 0;
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;                                          // the process list could not be read this time: no verdict
        }

        if (there)
        {
            (_seen, _missing) = (true, 0);
            return;
        }

        if (!_seen)
        {
            return;                                          // started by hand with --with-sim before the sim: wait for it
        }

        if (++_missing == 2)
        {
            _log("the simulator has quit; stopping with it (started with --with-sim)");
            _quit();
        }
    }
}
