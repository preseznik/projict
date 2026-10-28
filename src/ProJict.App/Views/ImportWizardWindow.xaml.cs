using System.Windows;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

public partial class ImportWizardWindow : Window
{
    public ImportWizardWindow(ImportWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
