using System.Runtime.InteropServices;

namespace GlassLink.Sim;

/// <summary>
/// Real input events for the simulator: it ignores synthetic window messages and SetCursorPos, so the mouse is moved
/// and keys are pressed the way hardware would. Used only while popping a display out.
/// </summary>
public static class Input
{
    /// <summary>Makes a window the foreground window; an Alt tap lifts Windows' foreground lock when it refuses.</summary>
    public static bool BringToFront(nint hwnd)
    {
        for (var i = 0; i < 5; i++)
        {
            if (!Native.SetForegroundWindow(hwnd))
            {
                Key(Native.VK_MENU, down: true);
                Key(Native.VK_MENU, down: false);
            }

            Thread.Sleep(400);
            if (Native.GetForegroundWindow() == hwnd)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Right-Alt + left click at a screen point, the sim's gesture for popping an instrument out.</summary>
    public static bool RightAltClick(nint simWindow, int x, int y)
    {
        if (!BringToFront(simWindow))
        {
            return false;
        }

        for (var i = 0; i < 8; i++)                      // a small sweep so the sim shows its cursor and hovers the display
        {
            MoveTo(x - 8 + i, y);
            Thread.Sleep(50);
        }

        Thread.Sleep(400);
        var (altDown, buttonDown) = (false, false);
        try
        {
            Key(Native.VK_RMENU, down: true, extended: true);
            altDown = true;
            Thread.Sleep(300);
            if (Native.GetForegroundWindow() != simWindow)
            {
                return false;                                // another window came up meanwhile: the click would land in it
            }

            Mouse(Native.MOUSEEVENTF_LEFTDOWN);
            buttonDown = true;
            Thread.Sleep(120);
        }
        finally
        {
            // always released, also after an exception or a thread abort mid-way: a Right-Alt left down in the sim
            // turns every later click into a pop-out (#39)
            if (buttonDown)
            {
                Mouse(Native.MOUSEEVENTF_LEFTUP);
                Thread.Sleep(200);
            }

            if (altDown)
            {
                Key(Native.VK_RMENU, down: false, extended: true);
            }
        }

        return true;
    }

    /// <summary>Presses a combination such as "shift+f1" in the sim (the user's own key for their flying view).</summary>
    public static bool SendCombo(nint simWindow, string combo)
    {
        var keys = ParseCombo(combo);
        if (!BringToFront(simWindow))
        {
            return false;
        }

        var pressed = new Stack<ushort>();
        try
        {
            foreach (var vk in keys)
            {
                Key(vk, down: true);
                pressed.Push(vk);
                Thread.Sleep(60);
            }

            Thread.Sleep(100);
        }
        finally
        {
            while (pressed.TryPop(out var vk))                // every key that went down comes up again (#39)
            {
                Key(vk, down: false);
                Thread.Sleep(60);
            }
        }

        return true;
    }

    /// <summary>"shift+f1" -> virtual key codes, modifiers first. Keys: a-z, 0-9, f1-f24.</summary>
    public static ushort[] ParseCombo(string combo)
    {
        var modifiers = new List<ushort>();
        var keys = new List<ushort>();
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.ToLowerInvariant()))
        {
            switch (part)
            {
                case "shift": modifiers.Add(0x10); break;
                case "ctrl" or "control": modifiers.Add(0x11); break;
                case "alt": modifiers.Add(0x12); break;
                case [var c] when char.IsAsciiDigit(c) || c is >= 'a' and <= 'z': keys.Add(char.ToUpperInvariant(c)); break;
                case ['f', .. var number] when int.TryParse(number, out var n) && n is >= 1 and <= 24: keys.Add((ushort)(0x70 + n - 1)); break;
                default: throw new FormatException($"unknown key '{part}' (use shift, ctrl, alt, a-z, 0-9, f1-f24)");
            }
        }

        if (keys.Count != 1)
        {
            throw new FormatException("a key combination needs exactly one key besides shift/ctrl/alt");
        }

        return [.. modifiers, .. keys];
    }

    public static (int X, int Y) Cursor => Native.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    public static bool IsDown(ushort virtualKey) => (Native.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>Absolute moves are scaled over the whole virtual desktop, so a sim on a second monitor is clicked where
    /// it is, not where the same numbers fall on the primary monitor (#42).</summary>
    private static void MoveTo(int x, int y)
    {
        var (vx, vy) = (Native.GetSystemMetrics(76), Native.GetSystemMetrics(77));           // SM_X/YVIRTUALSCREEN
        var (w, h) = (Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));             // SM_CX/CYVIRTUALSCREEN
        var input = new Native.INPUT { type = 0 };
        input.u.mi = new Native.MOUSEINPUT
        {
            dx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, w - 1)), dy = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, h - 1)),
            dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK,
        };
        Native.SendInput(1, [input], Marshal.SizeOf<Native.INPUT>());
    }

    private static void Mouse(uint flags)
    {
        var input = new Native.INPUT { type = 0 };
        input.u.mi = new Native.MOUSEINPUT { dwFlags = flags };
        Native.SendInput(1, [input], Marshal.SizeOf<Native.INPUT>());
    }

    private static void Key(ushort vk, bool down, bool extended = false)
    {
        var input = new Native.INPUT { type = 1 };
        input.u.ki = new Native.KEYBDINPUT
        {
            wVk = vk, wScan = (ushort)Native.MapVirtualKey(vk, 0),
            dwFlags = (down ? 0 : Native.KEYEVENTF_KEYUP) | (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0),
        };
        Native.SendInput(1, [input], Marshal.SizeOf<Native.INPUT>());
    }

    private static class Native
    {
        public const ushort VK_MENU = 0x12, VK_RMENU = 0xA5;
        public const uint MOUSEEVENTF_MOVE = 1, MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public nint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public nint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    }
}
