namespace GlassLink.Core.Usb;

/// <summary>The byte pipe to one DU. Implemented by WinUSB for real hardware and by a fake for tests.</summary>
public interface IDuTransport : IDisposable
{
    /// <summary>The DU's serial number (24 hex characters), its identity in the configuration.</summary>
    string Serial { get; }

    string Description { get; }

    /// <summary>The next bulk-IN transfer (any length, possibly part of a message); null on timeout.
    /// Throws <see cref="IOException"/> when the DU is gone.</summary>
    byte[]? ReadChunk(int timeoutMs);

    /// <summary>Sends one complete message. Safe to call from several threads.</summary>
    void Write(ReadOnlySpan<byte> data);
}
