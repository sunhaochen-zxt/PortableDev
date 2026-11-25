using System.IO.Pipes;
using PortableDev.Core.Logging;
using PortableDev.Core.Protocol;

namespace PortableDev.Kernel.Transport;

/// <summary>Accepts pipe connections for this stick; one kernel instance, many client connections (spec 3.1).</summary>
public sealed class PipeServer
{
    private const int MaxInstances = 16;

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
        _logger.Info("pipe.listen", "listening for connections", new System.Text.Json.Nodes.JsonObject
        {
            ["pipe"] = FullPipeName,
        });

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
                _logger.Error("pipe.create", "cannot create the pipe instance", ex);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            catch (IOException ex)
            {
                _logger.Warn("pipe.accept", "connection attempt failed", new System.Text.Json.Nodes.JsonObject { ["error"] = ex.Message });
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
                    _logger.Warn("pipe.protocol", "connection closed: " + ex.Message, new System.Text.Json.Nodes.JsonObject
                    {
                        ["connection"] = connectionId,
                    });
                }
                catch (IOException)
                {
                    // Peer disappeared.
                }
                finally
                {
                    server.Dispose();
                }
            }, CancellationToken.None);
        }

        _logger.Info("pipe.stop", "stopped listening", null);
    }
}
