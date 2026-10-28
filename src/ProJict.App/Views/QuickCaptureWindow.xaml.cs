using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProJict.App.ViewModels;
using ProJict.Core.Import;
using ProJict.Core.Model;

namespace ProJict.App.Views;

/// <summary>Small always-on-top window opened by the global hotkey: drop an item into any project/category.</summary>
public partial class QuickCaptureWindow : Window
{
    private readonly AppState _state;

    public QuickCaptureWindow(AppState state, MainViewModel main)
    {
        InitializeComponent();
        _state = state;

        var projects = state.Projects.Where(p => p.Status != ProjectStatus.Archived)
            .OrderBy(p => p.IsInbox ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        ProjectBox.ItemsSource = projects;
        // Default: the remembered target, else the project open in the main window, else the Inbox.
        ProjectBox.SelectedItem = projects.FirstOrDefault(p => p.Id == state.Settings.LastCaptureProjectId)
                                  ?? (main.CurrentProject?.Project is { } current && projects.Contains(current) ? current : null)
                                  ?? state.Inbox;

        Loaded += (_, _) =>
        {
            Activate();
            ItemText.Focus();
        };
        Deactivated += (_, _) =>
        {
            if (!_closing && ItemText.Text.Trim().Length == 0) Close();
        };
    }

    private bool _closing;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void ProjectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectBox.SelectedItem is not Project project) return;
        var names = project.Categories.Select(c => c.Name).ToList();
        CategoryBox.ItemsSource = names;
        var remembered = project.Id == _state.Settings.LastCaptureProjectId ? _state.Settings.LastCaptureCategory : null;
        CategoryBox.Text = names.FirstOrDefault(n => string.Equals(n, remembered, StringComparison.OrdinalIgnoreCase))
                           ?? names.FirstOrDefault() ?? (project.IsInbox ? "IDEAS" : "GENERAL");
    }

    private void ItemText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                if (!Add()) return;
                if (Keyboard.Modifiers == ModifierKeys.Shift) ItemText.Clear();
                else Close();
                break;
        }
    }

    private bool Add()
    {
        if (ProjectBox.SelectedItem is not Project project) return false;
        var item = TxtListParser.ParseLine(ItemText.Text);
        if (item == null) return false;
        if (item.IsDone) item.DoneAt = DateTime.Now;

        var categoryName = string.IsNullOrWhiteSpace(CategoryBox.Text) ? (project.IsInbox ? "IDEAS" : "GENERAL") : CategoryBox.Text.Trim();
        var category = project.GetOrAddCategory(categoryName);
        category.Items.Add(item);

        _state.Settings.LastCaptureProjectId = project.Id;
        _state.Settings.LastCaptureCategory = category.Name;
        _state.MarkSettingsDirty();
        _state.NotifyReplaced(project);
        _state.StatusText = $"Added to {project.Name} › {category.Name}: {item.DisplayText()}";
        Hint.Text = $"Added to {project.Name} › {category.Name}.";
        return true;
    }
}
