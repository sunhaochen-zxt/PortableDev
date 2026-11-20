using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PortableDev.Gui.ViewModels;

namespace PortableDev.Gui.Views;

public partial class PackagesView : UserControl
{
    private readonly DispatcherTimer _debounce;

    public PackagesView()
    {
        InitializeComponent();

        // Typing searches on its own after a short pause. A button that must be pressed
        // before anything happens makes the page feel dead, and a search per keystroke
        // would hammer the network for nothing.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (DataContext is not PackagesViewModel vm) return;
            if (vm.Query.Trim().Length < 2) return;
            if (vm.SearchCommand.CanExecute(null)) vm.SearchCommand.Execute(null);
        };

        Loaded += (_, _) =>
        {
            QueryBox.Focus();
            QueryBox.CaretIndex = QueryBox.Text.Length;
        };
    }

    /// <summary>Enter in the search box runs the same search immediately.</summary>
    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _debounce.Stop();

        if (DataContext is not PackagesViewModel vm) return;
        if (vm.SearchCommand.CanExecute(null)) vm.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }
}
