using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using GlassLink.Core.Du;
using GlassLink.Core.Flash;

namespace GlassLink.Dmc;

/// <summary>What the board job is doing or did, for the Display units tab.</summary>
public sealed record BoardJob(string Port, string Kind, bool Running, bool Ok, double Progress, string Message, string? Chip, string? Board)
{
    public JsonObject ToJson() => new()
    {
        ["port"] = Port, ["kind"] = Kind, ["running"] = Running, ["ok"] = Ok, ["progress"] = Math.Round(Progress, 3),
        ["message"] = Message, ["chip"] = Chip, ["board"] = Board,
    };
}

/// <summary>
/// "Set up a new board": GlassLink's own flasher on a NANO's USB-C port (no ESP-IDF, no esptool, #82). Identify reads
/// the chip (revision, factory MAC: which DU it is) and lets it run again; Install writes the flash set for its chip
/// family (bootloader, partition table, boot selection, app; not NVS, so a DU keeps its serial and label) and starts
/// it. One job at a time.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BoardSetup(DmcRuntime dmc)
{
    private readonly object _gate = new();

    public BoardJob? Job { get; private set; }

    public JsonObject ToJson() => new()
    {
        ["ports"] = new JsonArray([.. BoardPorts.Find().Select(p => (JsonNode)new JsonObject { ["port"] = p.Port, ["bridge"] = p.BridgeSerial })]),
        ["job"] = Job?.ToJson(),
        ["version"] = dmc.FirmwareVersion,
    };

    /// <summary>Starts a job; false if one runs already.</summary>
    public bool Start(string port, bool install)
    {
        lock (_gate)
        {
            if (Job is { Running: true })
            {
                return false;
            }

            Job = new BoardJob(port, install ? "install" : "identify", true, false, 0, "connecting to the board's bootloader", null, null);
        }

        new Thread(() => Run(port, install)) { IsBackground = true, Name = "board-setup" }.Start();
        return true;
    }

    private void Run(string port, bool install)
    {
        var kind = install ? "install" : "identify";
        string? chip = null, board = null;
        void Say(string message, double progress = 0) => Job = new BoardJob(port, kind, true, false, progress, message, chip, board);
        try
        {
            using var link = new SerialBoardLink(port);
            var loader = new EspLoader(link, m => dmc.Log($"board {port}: {m}"));
            var facts = loader.Connect();
            chip = $"ESP32-P4 {facts.RevisionName}";
            board = Describe(facts);
            dmc.Log($"board {port}: {chip}, {board}");
            if (!install)
            {
                loader.HardReset();
                Job = new BoardJob(port, kind, false, true, 1, $"{chip}, {board}", chip, board);
                return;
            }

            var set = FlashSet.Load(dmc.FlashSetFolderFor(facts.Revision));
            if (!set.App.Fits(facts.Revision))
            {
                throw new InvalidDataException($"the firmware here is for {set.App.ChipRange}, this chip is {facts.RevisionName}");
            }

            loader.SetBaud(460_800);
            var total = set.Parts.Sum(p => (long)p.Data.Length);
            long done = 0;
            foreach (var part in set.Parts)
            {
                var before = done;
                loader.Write(part, f => Say($"writing {Path.GetFileName(part.Name)} (GlassLink {set.App.Version})", (before + f * part.Data.Length) / total));
                done += part.Data.Length;
            }

            loader.HardReset();
            dmc.Log($"board {port}: GlassLink {set.App.Version} installed");
            Job = new BoardJob(port, kind, false, true, 1, $"GlassLink {set.App.Version} installed; the board starts it now", chip, board);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            dmc.Log($"board {port}: {ex.Message}");
            Job = new BoardJob(port, kind, false, false, Job?.Progress ?? 0, ex is UnauthorizedAccessException ? $"{port} is in use by another program (a serial monitor?)" : ex.Message, chip, board);
        }
    }

    /// <summary>"DU2 (ff6932d7…)" for a board the DMC knows by its MAC, else what a new DU will be called.</summary>
    private string Describe(ChipFacts facts)
    {
        if (dmc.Dus.SerialOf(facts.Mac) is not { } serial)
        {
            return $"not a DU this DMC knows by its MAC (a new DU gets serial {facts.DuSerial[..8]}…)";
        }

        var label = dmc.Dus.Settings(serial).Label;
        return $"{(label.Length > 0 ? label : "a known DU")} ({serial[..Math.Min(8, serial.Length)]}…)";
    }
}
