using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace PortableDev.Gui.Infrastructure;

/// <summary>Command that disables itself while its work runs.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _busy;

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(() => { execute(); return Task.CompletedTask; }, canExecute)
    {
    }

    /// <summary>
    /// Raised when a command throws. An "async void" handler that throws otherwise either
    /// takes the whole process down through the dispatcher's unhandled-exception path or
    /// (worse) leaves the page looking idle after a failed button press. The shell
    /// subscribes and puts the message in the footer.
    /// </summary>
    public static event Action<Exception>? UnhandledError;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_busy && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_busy) return;
        _busy = true;
        Raise();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
        finally
        {
            _busy = false;
            Raise();
        }
    }

    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    internal static void Report(Exception ex)
    {
        var handler = UnhandledError;
        if (handler is null)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return;
        }

        try { handler(ex); } catch { /* never let error reporting throw */ }
    }
}

/// <summary>Command taking a string parameter (navigation).</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;
    private bool _busy;

    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_busy && parameter is T t && (_canExecute?.Invoke(t) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is not T t || _busy) return;
        _busy = true;
        try
        {
            _execute(t);
        }
        catch (Exception ex)
        {
            RelayCommand.Report(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
