using System.IO.Pipes;
using System.Text.Json.Nodes;
using PortableDev.Core.Logging;
using PortableDev.Core.Protocol;

namespace PortableDev.Kernel.Transport;

/// <summary>Accepts pipe connections for this stick; one kernel instance, many client connections (spec 3.1).</summary>
public sealed class PipeServer
{
    private const int MaxInstances = 64;
    private const int AcceptLoops = 16;

    private readonly string _pipeName;
    private readonly KernelLogger _logger;
    private readonly Func<Stream, string, ClientConnection> _connectionFactory;

    public PipeServer(string pipePrefix, string stickId, KernelLogger logger, Func<Stream, string, ClientConnection> connectionFactory)
    {
        _pipeName = $"{pipePrefix}-{stickId}";
        _logger = logger;
        _connectionFactory = connectionFactory;
    }

    public string PipeName => _pipeName;

    public string FullPipeName => $@"\\.\pipe\{_pipeName}";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.Info("pipe.listen", "listening for connections", new JsonObject
        {
            ["pipe"] = FullPipeName,
            ["instances"] = MaxInstances,
            ["acceptLoops"] = AcceptLoops,
        });

        // Pre-create the whole pool: a single accept loop leaves a window without a listening
        // instance in which a client sees "all pipe instances are busy".
        var loops = Enumerable.Range(0, AcceptLoops).Select(_ => AcceptLoopAsync(cancellationToken)).ToArray();
        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.Info("pipe.stop", "stopped listening", null);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    MaxInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException ex)
            {
                // Capacity is full or the name is briefly unavailable; wait for a slot.
                _logger.Debug("pipe.create", "cannot create a pipe instance yet", new JsonObject { ["error"] = ex.Message });
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                return;
            }
            catch (IOException ex)
            {
                _logger.Warn("pipe.accept", "connection attempt failed", new JsonObject { ["error"] = ex.Message });
                server.Dispose();
                continue;
            }

            var connectionId = Guid.NewGuid().ToString("N")[..8];
            var connection = _connectionFactory(server, connectionId);
            _ = Task.Run(async () =>
            {
                try
                {
                    await connection.RunAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ProtocolException ex)
                {
                    _logger.Warn("pipe.protocol", "connection closed: " + ex.Message, new JsonObject
                    {
                        ["connection"] = connectionId,
                    });
                }
                catch (OperationCanceledException)
                {
                    // Shutdown.
                }
                catch (IOException)
                {
                    // Peer disappeared.
                }
                catch (Exception ex)
                {
                    _logger.Error("pipe.connection", "connection handler threw", ex, new JsonObject
                    {
                        ["connection"] = connectionId,
                    });
                }
                finally
                {
                    server.Dispose();
                }
            }, CancellationToken.None);
        }
    }
}
