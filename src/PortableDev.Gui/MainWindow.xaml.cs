using System;
using System.Windows;
using PortableDev.Gui.ViewModels;

namespace PortableDev.Gui;

public partial class MainWindow : Window
{
    private readonly AppViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new AppViewModel();
        DataContext = _vm;

        // A launcher that starts us from a console-less parent can hand over a hidden
        // STARTUPINFO, leaving the window minimised off-screen. Force a normal state
        // on first load; this is cheap and harmless when we were shown normally.
        Loaded += async (_, _) =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            await _vm.RefreshAllAsync();
        };
    }
}
