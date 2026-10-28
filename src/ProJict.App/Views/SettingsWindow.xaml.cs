using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProJict.App.Services;
using ProJict.Core.Export;

namespace ProJict.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppState _state;

    public SettingsWindow(AppState state)
    {
        InitializeComponent();
        _state = state;
        var s = state.Settings;
        DataRoot.Text = state.Store.DataRoot;
        MirrorEnabled.IsChecked = s.MirrorEnabled;
        MirrorFolder.Text = s.MirrorFolder ?? Path.Combine(Path.GetDirectoryName(state.Store.DataRoot) ?? "", "ProJict-mirror");
        Hotkey.Text = s.Hotkey;
        CloseToTray.IsChecked = s.CloseToTray;
        MarkInProgress.IsChecked = s.MarkCopiedInProgress;
        Theme.SelectedIndex = s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        StaleDays.Text = s.StaleDays.ToString();
        Groups.Text = string.Join(", ", s.Groups);
        VsCodePath.Text = s.VsCodePath ?? "";
        CheckUpdates.IsChecked = state.AppSettings.CheckForUpdates;
        GitHubAccount.Text = s.GitHubAccount ?? "";
        UseGitHubCli.IsChecked = state.AppSettings.UseGitHubCliSignIn;
        VersionText.Text = $"ProJict {UpdateService.CurrentVersionText}";
        UpdatePanel.DataContext = App.Current.MainVm.Updates;
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNow.IsEnabled = false;
        UpdateResult.Text = "Checking…";
        var updates = App.Current.MainVm.Updates;
        var result = await updates.CheckAsync(manual: true);
        // A found update shows in the panel below, with its install button.
        UpdateResult.Text = updates.IsAvailable ? "" : result;
        CheckNow.IsEnabled = true;
    }

    private async void CheckReleases_Click(object sender, RoutedEventArgs e)
    {
        // Checking uses what is typed here, so keep it even if the window is then cancelled.
        ApplyGitHubSettings();
        _state.MarkSettingsDirty();
        _state.SaveAppSettings();
        CheckReleases.IsEnabled = false;
        ReleasesResult.Text = "Checking GitHub…";
        ReleasesResult.Text = await App.Current.MainVm.GitHub.RefreshAsync(force: true);
        CheckReleases.IsEnabled = true;
    }

    private void ApplyGitHubSettings()
    {
        _state.Settings.GitHubAccount = string.IsNullOrWhiteSpace(GitHubAccount.Text) ? null : GitHubAccount.Text.Trim();
        _state.AppSettings.UseGitHubCliSignIn = UseGitHubCli.IsChecked == true;
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            Error.Text = "Use at least one modifier (Ctrl, Alt, Shift or Win).";
            return;
        }
        Error.Text = "";
        Hotkey.Text = HotkeyService.Format(Keyboard.Modifiers, key);
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e) => Hotkey.Text = "";

    private void BrowseData_Click(object sender, RoutedEventArgs e) => BrowseFolder(DataRoot, "ProJict data folder");
    private void BrowseMirror_Click(object sender, RoutedEventArgs e) => BrowseFolder(MirrorFolder, "TXT mirror folder");

    private void BrowseFolder(TextBox target, string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private void BrowseCode_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Code.exe|Code.exe|Programs|*.exe", Title = "VS Code executable" };
        if (dialog.ShowDialog(this) == true) VsCodePath.Text = dialog.FileName;
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => ShellLauncher.OpenFolder(_state.Store.DataRoot);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(StaleDays.Text, out var stale) || stale < 0)
        {
            Error.Text = "Stale days must be a whole number (0 turns it off).";
            return;
        }
        var mirrorOn = MirrorEnabled.IsChecked == true;
        if (mirrorOn && string.IsNullOrWhiteSpace(MirrorFolder.Text))
        {
            Error.Text = "Choose a folder for the mirror.";
            return;
        }

        var s = _state.Settings;
        var wasMirroring = s.MirrorEnabled && s.MirrorFolder == MirrorFolder.Text.Trim();
        s.MirrorEnabled = mirrorOn;
        s.MirrorFolder = string.IsNullOrWhiteSpace(MirrorFolder.Text) ? null : MirrorFolder.Text.Trim();
        s.CloseToTray = CloseToTray.IsChecked == true;
        s.MarkCopiedInProgress = MarkInProgress.IsChecked == true;
        s.StaleDays = stale;
        s.Theme = ((ComboBoxItem)Theme.SelectedItem).Content.ToString()!;
        s.VsCodePath = string.IsNullOrWhiteSpace(VsCodePath.Text) ? null : VsCodePath.Text.Trim();
        var groups = Groups.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count > 0) s.Groups = groups;
        s.EnsureDefaultGroups();

        App.ApplyTheme(s.Theme);
        _state.AppSettings.CheckForUpdates = CheckUpdates.IsChecked == true;
        ApplyGitHubSettings();
        _state.SaveAppSettings();

        if (Hotkey.Text != s.Hotkey || !string.IsNullOrEmpty(Hotkey.Text))
        {
            if (!App.Current.RegisterHotkey(Hotkey.Text, out var error))
            {
                Error.Text = error;
                App.Current.RegisterHotkey(s.Hotkey, out _);
                return;
            }
            s.Hotkey = Hotkey.Text;
        }

        _state.MarkSettingsDirty();
        _state.NotifyListChanged();

        if (mirrorOn && !wasMirroring)
        {
            _state.SaveNow();
            var result = ExportService.Export(_state.Projects, s.MirrorFolder!, s.Export, overwriteConflicts: false);
            _state.StatusText = $"Mirror: {result.Written.Count} written" +
                                (result.Conflicts.Count > 0 ? $", {result.Conflicts.Count} skipped (files not written by ProJict)" : "");
        }

        var newRoot = DataRoot.Text.Trim();
        if (!string.Equals(Path.GetFullPath(newRoot), _state.Store.DataRoot, StringComparison.OrdinalIgnoreCase))
        {
            _state.AppSettings.DataRoot = newRoot;
            _state.SaveAppSettings();
            MessageBox.Show(this, "Restart ProJict to use the new data folder.", "Data folder changed", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        DialogResult = true;
    }
}
