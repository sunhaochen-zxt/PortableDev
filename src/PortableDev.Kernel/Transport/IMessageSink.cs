using PortableDev.Core.Protocol;

namespace PortableDev.Kernel.Transport;

/// <summary>Minimal outbound interface shared by client and extension connections.</summary>
public interface IMessageSink
{
    string SessionId { get; }

    Task SendAsync(Envelope envelope);
}
