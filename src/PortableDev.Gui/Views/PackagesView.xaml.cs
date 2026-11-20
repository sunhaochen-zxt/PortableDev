using System.Windows.Controls;
using System.Windows.Input;
using PortableDev.Gui.ViewModels;

namespace PortableDev.Gui.Views;

public partial class PackagesView : UserControl
{
    public PackagesView()
    {
        InitializeComponent();
    }

    /// <summary>Enter in the search box runs the same command as the button.</summary>
    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not PackagesViewModel vm) return;

        if (vm.SearchCommand.CanExecute(null)) vm.SearchCommand.Execute(null);
        e.Handled = true;
    }
}
