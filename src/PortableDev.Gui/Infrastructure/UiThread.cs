using System;
using System.Threading.Tasks;
using System.Windows;

namespace PortableDev.Gui.Infrastructure;

/// <summary>
/// The one place that knows how to get onto the WPF dispatcher.
///
/// ObservableCollection and PropertyChanged are thread-affine. Any mutation that may
/// originate off the UI thread - a process output callback, a background refresh - has to
/// be marshalled here, or the window throws NotSupportedException at an unpredictable
/// moment. Relying on "the callers happen to resume on the UI thread" is not a contract.
/// </summary>
public static class UiThread
{
    /// <summary>Run an action on the UI thread and wait for it.</summary>
    public static Task InvokeAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    /// <summary>
    /// Fire-and-forget variant for streamed output lines: order is preserved because the
    /// dispatcher queue is FIFO, and a busy UI must not block a process reader.
    /// </summary>
    public static void Post(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
