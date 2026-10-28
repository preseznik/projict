using System.Windows;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

public partial class PatchNotesWindow : Window
{
    private readonly PatchNotesViewModel _vm;

    public PatchNotesWindow(PatchNotesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CreateRelease(out var error)) DialogResult = true;
        else Error.Text = error;
    }
}
