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

    /// <summary>The newest frame, or null before the first one.</summary>
    Frame? Latest { get; }

    /// <summary>Waits until a frame newer than <paramref name="afterSeq"/> exists; null on timeout.
    /// Must be a real predicate wait: a signal from a frame that was already sent must not satisfy it.</summary>
    Frame? WaitNewer(uint afterSeq, int timeoutMs);
}

/// <summary>A frame source fed by whoever produces pictures (the capture layer, a test pattern).</summary>
public sealed class FrameSlot(string name) : IFrameSource
{
    private readonly object _gate = new();
    private Frame? _latest;

    public string Name { get; } = name;

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
    private int _clients;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public DateTime? LastPublished { get; private set; }

    /// <summary>Viewers on the network (WebSocket, MJPEG) that are looking at this display right now.</summary>
    public int Clients => Volatile.Read(ref _clients);

    public void AddClient() => Interlocked.Increment(ref _clients);

    public void RemoveClient() => Interlocked.Decrement(ref _clients);

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

    /// <summary>Publishes a new picture; returns its sequence number.</summary>
    public uint Publish(ReadOnlyMemory<byte> jpeg, int width = 0, int height = 0)
    {
        lock (_gate)
        {
            (Width, Height) = (width > 0 ? width : Width, height > 0 ? height : Height);
            LastPublished = DateTime.UtcNow;
            _times.Enqueue(Environment.TickCount64);
            while (_times.Count > 200)
            {
                _times.Dequeue();
            }

            _latest = new Frame((_latest?.Seq ?? 0) + 1, jpeg);
            Monitor.PulseAll(_gate);
            return _latest.Seq;
        }
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
