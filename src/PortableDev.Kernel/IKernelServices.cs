using PortableDev.Core.Config;
using PortableDev.Core.Extensions;
using PortableDev.Core.Logging;
using PortableDev.Kernel.Dispatch;
using PortableDev.Kernel.Supervisor;
using PortableDev.Kernel.Transport;

namespace PortableDev.Kernel;

/// <summary>What every kernel component may reach; implemented by <see cref="KernelHost"/>.</summary>
public interface IKernelServices
{
    KernelState State { get; }

    KernelPaths Paths { get; }

    SettingsStore Settings { get; }

    ExtensionRegistry Registry { get; }

    KernelLogger Logger { get; }

    SessionRegistry Sessions { get; }

    ExtensionSupervisor Supervisor { get; }

    CommandRouter Router { get; }

    /// <summary>Effective pipe prefix: --pipe-prefix beats settings.</summary>
    string PipePrefix { get; }

    Task RequestShutdownAsync(bool force);
}
