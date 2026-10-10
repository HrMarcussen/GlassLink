using System.IO.Ports;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GlassLink.Core.Flash;

/// <summary>A NANO's USB-C serial port: its COM name and the USB-serial bridge's own serial number.</summary>
public sealed record BoardPort(string Port, string BridgeSerial);

/// <summary>
/// The serial ports of ESP32-P4-NANO boards: the WCH CH343 bridge on the NANO's USB-C socket (USB VID 1A86, PID 55D3).
/// Nothing else is ever offered: a PC with a cockpit has other serial devices (panels, Arduinos, a Pico) that a reset
/// pulse on DTR/RTS must never reach.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BoardPorts
{
    public const string BridgeId = @"VID_1A86&PID_55D3";

    public static IReadOnlyList<BoardPort> Find()
    {
        var present = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<BoardPort>();
        using var bridges = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\USB\{BridgeId}");
        foreach (var instance in bridges?.GetSubKeyNames() ?? [])
        {
            using var parameters = bridges!.OpenSubKey($@"{instance}\Device Parameters");
            if (parameters?.GetValue("PortName") is string port && present.Contains(port))
            {
                found.Add(new BoardPort(port, instance));
            }
        }

        return [.. found.OrderBy(p => p.Port, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Only a port that <see cref="Find"/> lists may be opened by the flasher.</summary>
    public static bool IsBoard(string port) => Find().Any(p => string.Equals(p.Port, port, StringComparison.OrdinalIgnoreCase));
}
