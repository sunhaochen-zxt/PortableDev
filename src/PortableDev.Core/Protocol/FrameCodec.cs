using System.Buffers;
using System.Buffers.Binary;

namespace PortableDev.Core.Protocol;

/// <summary>Length-prefixed framing: [4-byte little-endian length][UTF-8 JSON] (spec 3.2).</summary>
public static class FrameCodec
{
    /// <summary>
    /// Reads one frame. Returns <c>null</c> on a clean end of stream (the peer closed between frames).
    /// </summary>
    /// <exception cref="ProtocolException">On a short header, an empty frame or a frame above the limit.</exception>
    public static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, int maxFrameBytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await ReadAtLeastAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < header.Length)
        {
            throw new ProtocolException("connection closed inside a frame header");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0)
        {
            throw new ProtocolException("zero-length frame");
        }

        if (length > maxFrameBytes)
        {
            throw new ProtocolException($"frame of {length} bytes exceeds the {maxFrameBytes} byte limit");
        }

        var payload = new byte[length];
        var got = await ReadAtLeastAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (got < payload.Length)
        {
            throw new ProtocolException("connection closed inside a frame body");
        }

        return payload;
    }

    /// <summary>Writes one frame. The caller owns flushing.</summary>
    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length == 0)
        {
            throw new ProtocolException("refusing to write an empty frame");
        }

        if (payload.Length > ProtocolConstants.MaxFrameBytes)
        {
            throw new ProtocolException($"frame of {payload.Length} bytes exceeds the {ProtocolConstants.MaxFrameBytes} byte limit");
        }

        var rented = ArrayPool<byte>.Shared.Rent(payload.Length + 4);
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rented.AsSpan(0, 4), (uint)payload.Length);
            payload.Span.CopyTo(rented.AsSpan(4));
            await stream.WriteAsync(rented.AsMemory(0, payload.Length + 4), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async ValueTask<int> ReadAtLeastAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
