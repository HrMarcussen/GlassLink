using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GlassLink.Core.Usb;

/// <summary>
/// A DU over WinUSB, Windows' own generic USB driver (the DU asks for it with Microsoft OS 2.0 descriptors, so there
/// is nothing to install and no third-party library in between).
///
/// Reading is done by one thread that always has a read pending and never cancels it. Reads with a timeout would be
/// cancelled when the timeout fires, and a cancelled bulk read can lose the bytes that arrived at that moment.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinUsbTransport : IDuTransport
{
    /// <summary>DeviceInterfaceGUID from the DU's MS OS 2.0 descriptor.</summary>
    public static readonly Guid InterfaceGuid = new("B7E8A4C2-6F0D-4E21-9C3A-5D2F1E8B7A60");

    private const int ReadBufferSize = 64 * 1024;
    private readonly SafeFileHandle _file;
    private readonly nint _usb;
    private readonly byte _pipeIn;
    private readonly byte _pipeOut;
    private readonly BlockingCollection<byte[]> _chunks = new(new ConcurrentQueue<byte[]>(), 256);
    private readonly Thread _reader;
    private readonly object _writeLock = new();
    private volatile Exception? _readError;
    private volatile bool _closing;

    public string Serial { get; }

    public string Description { get; }

    /// <summary>Device paths of all DUs that are plugged in right now.</summary>
    public static IReadOnlyList<string> FindDevicePaths()
    {
        var guid = InterfaceGuid;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (Native.CM_Get_Device_Interface_List_Size(out var size, ref guid, null, Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0 || size <= 1)
            {
                return [];
            }

            var buffer = new char[size];
            var result = Native.CM_Get_Device_Interface_List(ref guid, null, buffer, size, Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            if (result == Native.CR_BUFFER_SMALL)
            {
                continue;                   // a device arrived between the two calls
            }

            if (result != 0)
            {
                return [];
            }

            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        return [];
    }

    /// <summary>\\?\usb#vid_303a&amp;pid_4001#1501f789...#{guid} -> 1501f789...</summary>
    public static string SerialFromPath(string devicePath) => DevicePaths.Serial(devicePath);

    public WinUsbTransport(string devicePath)
    {
        Serial = SerialFromPath(devicePath);
        _file = Native.CreateFile(devicePath, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            0, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, 0);
        if (_file.IsInvalid)
        {
            throw new IOException($"cannot open DU {Serial}: {new Win32Exception(Marshal.GetLastWin32Error()).Message} (is another DMC using it?)");
        }

        if (!Native.WinUsb_Initialize(_file, out _usb))
        {
            var error = Marshal.GetLastWin32Error();
            _file.Dispose();
            throw new IOException($"WinUSB could not take DU {Serial}: {new Win32Exception(error).Message}");
        }

        try
        {
            if (!Native.WinUsb_QueryInterfaceSettings(_usb, 0, out var itf))
            {
                throw new IOException("cannot read the DU's interface descriptor");
            }

            for (byte i = 0; i < itf.bNumEndpoints; i++)
            {
                if (Native.WinUsb_QueryPipe(_usb, 0, i, out var pipe) && pipe.PipeType == Native.UsbdPipeTypeBulk)
                {
                    if ((pipe.PipeId & 0x80) != 0)
                    {
                        _pipeIn = pipe.PipeId;
                    }
                    else
                    {
                        _pipeOut = pipe.PipeId;
                    }
                }
            }

            if (_pipeIn == 0 || _pipeOut == 0)
            {
                throw new IOException("the DU has no bulk IN/OUT endpoint pair");
            }

            SetPolicy(_pipeOut, Native.SHORT_PACKET_TERMINATE, 1);     // a transfer of n x 512 bytes ends with a zero-length packet
            SetPolicy(_pipeOut, Native.PIPE_TRANSFER_TIMEOUT, 2000);
            SetPolicy(_pipeOut, Native.AUTO_CLEAR_STALL, 1);
            SetPolicy(_pipeIn, Native.PIPE_TRANSFER_TIMEOUT, 0);       // the reader waits for ever; closing the handle ends it
            SetPolicy(_pipeIn, Native.AUTO_CLEAR_STALL, 1);
        }
        catch
        {
            Native.WinUsb_Free(_usb);
            _file.Dispose();
            throw;
        }

        Description = $"GlassLink DU {Serial[..Math.Min(8, Serial.Length)]} (WinUSB, pipes 0x{_pipeIn:x2}/0x{_pipeOut:x2})";
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"du-read-{Serial[..Math.Min(8, Serial.Length)]}" };
        _reader.Start();
    }

    public byte[]? ReadChunk(int timeoutMs)
    {
        if (_chunks.TryTake(out var chunk, timeoutMs))
        {
            return chunk;
        }

        if (_readError is { } error)
        {
            throw new IOException($"DU {Serial} disconnected: {error.Message}", error);
        }

        return null;
    }

    public void Write(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        lock (_writeLock)                                // the two parts of one message stay together
        {
            Write(header);
            if (payload.Length > 0)
            {
                Write(payload);
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var offset = 0;
            while (offset < data.Length)            // WinUSB takes the whole buffer in one call; loop only for safety
            {
                if (!Native.WinUsb_WritePipe(_usb, _pipeOut, in data[offset], (uint)(data.Length - offset), out var written, 0))
                {
                    throw new IOException($"write to DU {Serial} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                }

                offset += (int)written;
            }
        }
    }

    public void Dispose()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Native.WinUsb_AbortPipe(_usb, _pipeOut);          // a write that is still timing out returns at once
        // The pending read returns at once too; but a reader that passed its _closing check just before can start a new
        // read after one abort, so it is aborted until the reader has gone (review L6)
        for (var i = 0; i < 10 && _reader.IsAlive; i++)
        {
            Native.WinUsb_AbortPipe(_usb, _pipeIn);
            _reader.Join(100);
        }

        if (_reader.IsAlive)
        {
            return;                                      // freeing the handles under a read in progress would crash: kept
        }

        lock (_writeLock)                                // and no write is running: only now may the handles go (#31)
        {
            Native.WinUsb_Free(_usb);
            _file.Dispose();
        }

        _chunks.Dispose();
    }

    /// <summary>A policy that does not take is an error: without the zero-length packet every message of n x 512 bytes
    /// would stall, without the timeout a write to a hung DU would wait for ever (review L7).</summary>
    private void SetPolicy(byte pipe, uint policy, uint value)
    {
        bool ok;
        if (policy is Native.SHORT_PACKET_TERMINATE or Native.AUTO_CLEAR_STALL)
        {
            var flag = (byte)value;
            ok = Native.WinUsb_SetPipePolicy(_usb, pipe, policy, 1, ref flag);
        }
        else
        {
            ok = Native.WinUsb_SetPipePolicy(_usb, pipe, policy, 4, ref value);
        }

        if (!ok)
        {
            throw new IOException($"cannot set WinUSB pipe policy 0x{policy:x} on DU {Serial}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[ReadBufferSize];
        try
        {
            while (!_closing)
            {
                if (!Native.WinUsb_ReadPipe(_usb, _pipeIn, ref buffer[0], (uint)buffer.Length, out var got, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                if (got > 0)
                {
                    _chunks.Add(buffer.AsSpan(0, (int)got).ToArray());
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ObjectDisposedException)
        {
            _readError = _closing ? null : ex;
        }
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        public const uint SHORT_PACKET_TERMINATE = 0x01, AUTO_CLEAR_STALL = 0x02, PIPE_TRANSFER_TIMEOUT = 0x03;
        public const int UsbdPipeTypeBulk = 2;
        public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
        public const int CR_BUFFER_SMALL = 0x1A;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct UsbInterfaceDescriptor
        {
            public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass,
                bInterfaceSubClass, bInterfaceProtocol, iInterface;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PipeInformation
        {
            public int PipeType;
            public byte PipeId;
            public ushort MaximumPacketSize;
            public byte Interval;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_Initialize(SafeFileHandle device, out nint usb);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_Free(nint usb);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_QueryInterfaceSettings(nint usb, byte alternate, out UsbInterfaceDescriptor descriptor);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_QueryPipe(nint usb, byte alternate, byte index, out PipeInformation pipe);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_SetPipePolicy(nint usb, byte pipe, uint policy, uint length, ref byte value);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_SetPipePolicy(nint usb, byte pipe, uint policy, uint length, ref uint value);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_ReadPipe(nint usb, byte pipe, ref byte buffer, uint length, out uint transferred, nint overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_WritePipe(nint usb, byte pipe, in byte buffer, uint length, out uint transferred, nint overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_AbortPipe(nint usb, byte pipe);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_List_Size(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_List(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);
    }
}
