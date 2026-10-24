using System.Collections.Specialized;
using System.Windows.Controls;

namespace PortableDev.Gui.Views;

public partial class ProgressView : UserControl
{
    public ProgressView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is ViewModels.ProgressViewModel vm)
                ((INotifyCollectionChanged)vm.Lines).CollectionChanged += (_, _) =>
                    LogScroll.ScrollToEnd();
        };
    }
}
