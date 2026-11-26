using System.IO.Pipes;
using PortableDev.Core.Protocol;

namespace PortableDev.Shell;

/// <summary>The client half of the pipe transport (spec 3.1 / 3.2).</summary>
public sealed class PipeClient : IDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly bool _trace;
    private readonly CancellationToken _token;
    private bool _disposed;

    private PipeClient(Stream stream, bool trace, CancellationToken token)
    {
        _stream = stream;
        _trace = trace;
        _token = token;
    }

    public static async Task<PipeClient?> TryConnectAsync(string pipeName, int timeoutMs, bool trace, CancellationToken token)
    {
        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(timeoutMs, token).ConfigureAwait(false);
            if (trace)
            {
                Console.Error.WriteLine("[pd] connected to pipe " + pipeName);
            }

            return new PipeClient(stream, trace, token);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            stream.Dispose();
            return null;
        }
    }

    public async Task SendAsync(Envelope envelope)
    {
        var bytes = envelope.ToBytes();
        if (_trace)
        {
            Console.Error.WriteLine("[tx] " + envelope.Kind + " " + envelope.ToJson().ToJsonString());
        }

        await _writeGate.WaitAsync(_token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await FrameCodec.WriteFrameAsync(_stream, bytes, _token).ConfigureAwait(false);
            await _stream.FlushAsync(_token).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Emergency path: send a caller-supplied JSON document as one frame (architecture 3.7).</summary>
    public Task SendRawAsync(string json)
    {
        var envelope = Envelope.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        return SendAsync(envelope);
    }

    public async Task<Envelope?> ReceiveAsync()
    {
        try
        {
            var frame = await FrameCodec.ReadFrameAsync(_stream, ProtocolConstants.MaxFrameBytes, _token).ConfigureAwait(false);
            if (frame is null)
            {
                return null;
            }

            var envelope = Envelope.Parse(frame);
            if (_trace)
            {
                Console.Error.WriteLine("[rx] " + envelope.Kind + " " + envelope.ToJson().ToJsonString());
            }

            return envelope;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            // The pipe was closed while a read was pending (handshake timeout or shutdown).
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stream.Dispose();
        }
        catch (IOException)
        {
            // Already gone.
        }

        _writeGate.Dispose();
    }
}
