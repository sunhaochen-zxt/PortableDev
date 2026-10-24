using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PortableDev.Gui.Infrastructure;

/// <summary>Minimal INotifyPropertyChanged base. No MVVM framework: the UI is small and
/// every dependency here has to survive on a restored machine.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
