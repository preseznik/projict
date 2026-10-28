using System.IO;
using System.Windows;
using ProJict.App.Services;
using ProJict.Core.Export;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.App.Views;

public partial class ExportDialog : Window
{
    private readonly AppState _state;
    private readonly Project? _project;

    public ExportDialog(AppState state, Project? project)
    {
        InitializeComponent();
        _state = state;
        _project = project;

        if (project != null) CurrentOnly.Content = $"This project: {project.Name}";
        else
        {
            CurrentOnly.Visibility = Visibility.Collapsed;
            AllProjects.IsChecked = true;
        }
        Folder.Text = state.Settings.DefaultExportFolder(state.Store.DataRoot);
        EmptyCategories.IsChecked = state.Settings.Export.IncludeEmptyCategories;
        DoneInTodo.IsChecked = state.Settings.Export.IncludeDoneInTodo;
        ExportNotes.IsChecked = state.Settings.Export.ExportNotes;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Export folder" };
        if (Directory.Exists(Folder.Text)) dialog.InitialDirectory = Folder.Text;
        if (dialog.ShowDialog(this) == true) Folder.Text = dialog.FolderName;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(Folder.Text)) ShellLauncher.OpenFolder(Folder.Text);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var folder = Folder.Text.Trim();
        if (folder.Length == 0) return;
        try
        {
            folder = Path.GetFullPath(folder);
        }
        catch (Exception ex)
        {
            ShowResult(ex.Message);
            return;
        }

        if (_state.Settings.ImportSourceFolders.Any(f => SamePath(f, folder)) &&
            MessageBox.Show(this,
                "This is the folder your original TXT files were imported from.\n\nExporting here can replace those archive files " +
                "(you will be asked before any file ProJict did not write is overwritten). Continue?",
                "Export into the archive folder?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var options = new ExportOptions
        {
            IncludeEmptyCategories = EmptyCategories.IsChecked == true,
            IncludeDoneInTodo = DoneInTodo.IsChecked == true,
            ExportNotes = ExportNotes.IsChecked == true,
        };
        var projects = CurrentOnly.IsChecked == true && _project != null
            ? [_project]
            : _state.Projects.Where(p => IncludeArchived.IsChecked == true || p.Status != ProjectStatus.Archived).ToList();

        _state.SaveNow();
        try
        {
            var result = ExportService.Export(projects, folder, options, overwriteConflicts: false);
            if (result.Conflicts.Count > 0 &&
                MessageBox.Show(this,
                    $"{result.Conflicts.Count} file(s) already exist and were not written by ProJict (or were edited since):\n\n" +
                    string.Join("\n", result.Conflicts.Take(15)) + (result.Conflicts.Count > 15 ? "\n…" : "") +
                    "\n\nOverwrite them?", "Overwrite files?", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                var conflicting = projects.Where(p => ExportService.BuildFiles(p, options).Any(f => result.Conflicts.Contains(f.FileName))).ToList();
                var forced = ExportService.Export(conflicting, folder, options, overwriteConflicts: true);
                result.Written.AddRange(forced.Written.Except(result.Written));
                result.Conflicts.Clear();
                result.Errors.AddRange(forced.Errors);
            }

            var text = $"Exported to {folder}: {result.Written.Count} written, {result.Unchanged.Count} unchanged";
            if (result.Removed.Count > 0) text += $", {result.Removed.Count} old file(s) removed";
            if (result.Conflicts.Count > 0) text += $", {result.Conflicts.Count} skipped";
            if (result.Errors.Count > 0) text += ".\nErrors: " + string.Join("; ", result.Errors);
            ShowResult(text + ".");
            _state.StatusText = text;
        }
        catch (Exception ex)
        {
            ShowResult("Export failed: " + ex.Message);
            return;
        }

        _state.Settings.ExportFolder = folder;
        _state.Settings.Export = options;
        _state.MarkSettingsDirty();
    }

    private void ShowResult(string text)
    {
        Result.Text = text;
        Result.Visibility = Visibility.Visible;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
