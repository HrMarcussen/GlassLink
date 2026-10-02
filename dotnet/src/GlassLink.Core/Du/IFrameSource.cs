using System.Buffers;

namespace GlassLink.Core.Du;

/// <summary>One encoded picture of a display. Seq increases by one for every picture that differs from the last.</summary>
public sealed record Frame(uint Seq, ReadOnlyMemory<byte> Jpeg);

/// <summary>
/// Where a DU gets its pictures from: the newest frame of one display. The same object is kept for as long as the
/// display exists, so its sequence numbers never restart under a DU that is showing it.
/// </summary>
public interface IFrameSource
{
    string Name { get; }

    /// <summary>The display's name as people know it ("Captain PFD"): a DU shows it while it waits for the picture (#81).</summary>
    string Title => Name;

    /// <summary>False while the display cannot deliver pictures: its window is not there (the sim is not showing it). A
    /// DU then says it waits for the sim instead of keeping an old picture up (#81).</summary>
    bool Live => true;

    /// <summary>The newest frame, or null before the first one.</summary>
    Frame? Latest { get; }

    /// <summary>Waits until a frame newer than <paramref name="afterSeq"/> exists; null on timeout.
    /// Must be a real predicate wait: a signal from a frame that was already sent must not satisfy it.</summary>
    Frame? WaitNewer(uint afterSeq, int timeoutMs);
}

/// <summary>One display's place in a band: its box, relative to the band's top left corner.</summary>
public sealed record BandPart(IFrameSource Source, int X, int Y, int Width, int Height);

/// <summary>A frame source fed by whoever produces pictures (the capture layer, a test pattern).</summary>
public sealed class FrameSlot(string name) : IFrameSource
{
    private readonly object _gate = new();
    private Frame? _latest;

    public string Name { get; } = name;

    public string Title { get; set; } = name;

    private volatile bool _live = true;

    /// <summary>Set by the capture: true while it has the display's window. A slot fed by anything else stays live.</summary>
    public bool Live
    {
        get => _live;
        set => _live = value;
    }

    public Frame? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    private readonly Queue<long> _times = new();
    private readonly Dictionary<long, string> _viewers = [];
    private long _nextViewer;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public DateTime? LastPublished { get; private set; }

    /// <summary>Viewers on the network (WebSocket, MJPEG) that are looking at this display right now.</summary>
    public int Clients
    {
        get
        {
            lock (_viewers)
            {
                return _viewers.Count;
            }
        }
    }

    /// <summary>What the viewers are ("iPhone", "this PC"), for the status page to say where the display is shown.</summary>
    public IReadOnlyList<string> Viewers
    {
        get
        {
            lock (_viewers)
            {
                return [.. _viewers.Values];
            }
        }
    }

    /// <summary>A viewer starts looking; dispose the result when it stops.</summary>
    public IDisposable AddViewer(string device)
    {
        long id;
        lock (_viewers)
        {
            id = ++_nextViewer;
            _viewers[id] = device;
        }

        return new ViewerLease(this, id);
    }

    private sealed class ViewerLease(FrameSlot slot, long id) : IDisposable
    {
        public void Dispose()
        {
            lock (slot._viewers)
            {
                slot._viewers.Remove(id);
            }
        }
    }

    // The raw picture next to the JPEG, for a band that puts several displays into one picture (it would otherwise
    // decode our JPEG and encode it a second time). Kept only while someone wants it, in one buffer that is reused.
    private int _pixelUsers;
    private byte[] _pixels = [];
    private int _pixelWidth, _pixelHeight;
    private uint _pixelSeq;

    /// <summary>True while a band wants the raw pictures: the producer then passes them to <see cref="Publish"/>.</summary>
    public bool WantsPixels => Volatile.Read(ref _pixelUsers) > 0;

    public void AddPixelUser() => Interlocked.Increment(ref _pixelUsers);

    public void RemovePixelUser() => Interlocked.Decrement(ref _pixelUsers);

    /// <summary>Raised after every <see cref="Publish"/>, outside the lock (a band waits for it instead of polling).</summary>
    public event Action? Published;

    /// <summary>Hands the raw picture of frame <paramref name="seq"/> (tight BGRA) to <paramref name="use"/>, under
    /// the slot's lock so the producer cannot overwrite it meanwhile; false if the slot has no pixels of that frame.</summary>
    public bool ReadPixels(uint seq, ReadOnlySpanAction<byte, (int Width, int Height)> use)
    {
        lock (_gate)
        {
            if (_pixelSeq != seq || _pixelWidth == 0)
            {
                return false;
            }

            use(_pixels.AsSpan(0, _pixelWidth * _pixelHeight * 4), (_pixelWidth, _pixelHeight));
            return true;
        }
    }

    /// <summary>Pictures published per second over the last two seconds: how fast the content of the display changes.</summary>
    public double Fps()
    {
        lock (_gate)
        {
            var cutoff = Environment.TickCount64 - 2000;
            while (_times.Count > 0 && _times.Peek() < cutoff)
            {
                _times.Dequeue();
            }

            return _times.Count / 2.0;
        }
    }

    /// <summary>Publishes a new picture; returns its sequence number. <paramref name="pixels"/> (tight BGRA of
    /// width x height) is kept for <see cref="ReadPixels"/> when given.</summary>
    public uint Publish(ReadOnlyMemory<byte> jpeg, int width = 0, int height = 0, ReadOnlySpan<byte> pixels = default)
    {
        uint seq;
        lock (_gate)
        {
            if (!pixels.IsEmpty && width > 0 && height > 0 && pixels.Length >= width * height * 4)
            {
                if (_pixels.Length < width * height * 4)
                {
                    _pixels = new byte[width * height * 4];
                }

                pixels[..(width * height * 4)].CopyTo(_pixels);
                (_pixelWidth, _pixelHeight, _pixelSeq) = (width, height, (_latest?.Seq ?? 0) + 1);
            }

            (Width, Height) = (width > 0 ? width : Width, height > 0 ? height : Height);
            LastPublished = DateTime.UtcNow;
            _times.Enqueue(Environment.TickCount64);
            while (_times.Count > 200)
            {
                _times.Dequeue();
            }

            _latest = new Frame((_latest?.Seq ?? 0) + 1, jpeg);
            Monitor.PulseAll(_gate);
            seq = _latest.Seq;
        }

        Published?.Invoke();
        return seq;
    }

    public Frame? WaitNewer(uint afterSeq, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_gate)
        {
            while (_latest is null || _latest.Seq <= afterSeq)
            {
                var left = (int)(deadline - Environment.TickCount64);
                if (left <= 0 || !Monitor.Wait(_gate, left))
                {
                    return _latest is { } f && f.Seq > afterSeq ? f : null;
                }
            }

            return _latest;
        }
    }
}
