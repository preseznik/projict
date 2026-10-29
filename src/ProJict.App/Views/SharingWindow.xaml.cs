using System.Windows;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>Sharing your projects with other ProJict apps on the network, and connecting to theirs.</summary>
public partial class SharingWindow : Window
{
    private readonly SharingViewModel _vm;

    public SharingWindow(MainViewModel main, bool connectTab = false)
    {
        InitializeComponent();
        _vm = new SharingViewModel(main);
        DataContext = _vm;
        if (connectTab) Loaded += (_, _) => ((System.Windows.Controls.TabControl)((System.Windows.Controls.DockPanel)Content).Children[1]).SelectedIndex = 1;
        Closed += (_, _) => _vm.Detach();
    }
}
