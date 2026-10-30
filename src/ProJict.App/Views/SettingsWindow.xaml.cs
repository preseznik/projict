using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProJict.App.Services;
using ProJict.App.ViewModels;
using ProJict.Core.Export;
using ProJict.Core.Model;

namespace ProJict.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppState _state;

    public SettingsWindow(AppState state)
    {
        InitializeComponent();
        _state = state;
        MaxHeight = SystemParameters.WorkArea.Height - 24;
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
        AllowAgents.IsChecked = state.AppSettings.AllowAgents;
        foreach (var box in AgentPriorities.Children.OfType<CheckBox>())
            box.IsChecked = s.AgentPriorities.Contains(Enum.Parse<Priority>((string)box.Tag));
        UserName.Text = state.UserName;
        AgentClient.ItemsSource = AgentSetups(state);
        AgentClient.SelectedIndex = 0;
        GitHubAccount.Text = s.GitHubAccount ?? "";
        UseGitHubCli.IsChecked = state.AppSettings.UseGitHubCliSignIn;
        VersionText.Text = $"ProJict {UpdateService.CurrentVersionText}";
        UpdatePanel.DataContext = App.Current.MainVm.Updates;
        ApiStatus.Text = ApiViewModel.StatusOf(App.Current.MainVm.Api);
    }

    private void OpenApi_Click(object sender, RoutedEventArgs e)
    {
        new ApiWindow(App.Current.MainVm) { Owner = this }.ShowDialog();
        ApiStatus.Text = ApiViewModel.StatusOf(App.Current.MainVm.Api);
    }

    /// <summary>How to add ProJict to one AI agent: what to run or paste, and where.</summary>
    public sealed record AgentSetup(string Client, string Text, string Where);

    /// <summary>
    /// Setup for each supported agent, all starting "ProJict.exe --mcp" (plus this data folder when it isn't the
    /// remembered one). Formats: Claude Code and Codex CLI commands, Codex's config.toml, OpenCode's opencode.json and
    /// the mcpServers JSON most other clients use.
    /// </summary>
    public static IReadOnlyList<AgentSetup> AgentSetups(AppState state)
    {
        var exe = Environment.ProcessPath!;
        var remembered = state.AppSettings.DataRoot is { } r ? Path.GetFullPath(r) : null;
        string[] args = string.Equals(remembered, state.Store.DataRoot, StringComparison.OrdinalIgnoreCase)
            ? ["--mcp"] : ["--mcp", "--data", state.Store.DataRoot];
        var cliArgs = string.Join(" ", args.Select(a => a.Contains(' ') || a.Contains('\\') ? $"\"{a}\"" : a));
        // TOML literal strings ('…') take Windows paths as they are.
        var tomlArgs = string.Join(", ", args.Select(a => $"'{a}'"));
        var pretty = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var openCode = new System.Text.Json.Nodes.JsonObject
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["mcp"] = new System.Text.Json.Nodes.JsonObject
            {
                ["projict"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "local",
                    ["command"] = new System.Text.Json.Nodes.JsonArray([exe, .. args.Select(a => (System.Text.Json.Nodes.JsonNode)a)]),
                    ["enabled"] = true,
                },
            },
        }.ToJsonString(pretty);
        var mcpServers = new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
            {
                ["projict"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = exe,
                    ["args"] = new System.Text.Json.Nodes.JsonArray(args.Select(a => (System.Text.Json.Nodes.JsonNode)a).ToArray()),
                },
            },
        }.ToJsonString(pretty);
        return
        [
            new("Claude Code", $"claude mcp add --scope user projict -- \"{exe}\" {cliArgs}", "Run this once in a terminal."),
            new("OpenAI Codex", $"codex mcp add projict -- \"{exe}\" {cliArgs}\n\n# or add to ~/.codex/config.toml:\n[mcp_servers.projict]\ncommand = '{exe}'\nargs = [{tomlArgs}]",
                "Run the command once in a terminal, or add the table to ~/.codex/config.toml (the CLI and the IDE extension share it)."),
            new("OpenCode", openCode, "Merge into ~/.config/opencode/opencode.json (for all projects) or opencode.json in a project folder."),
            new("Claude Desktop & other MCP clients", mcpServers, "Merge into the client's MCP settings, e.g. claude_desktop_config.json for Claude Desktop."),
        ];
    }

    /// <summary>The Claude Code command (kept for checks that look for it).</summary>
    public static string McpRegistrationCommand(AppState state) => AgentSetups(state)[0].Text;

    private void AgentClient_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentClient.SelectedItem is not AgentSetup setup) return;
        McpCommand.Text = setup.Text;
        McpWhere.Text = setup.Where;
    }

    private void CopyMcpCommand_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(McpCommand.Text);
            _state.StatusText = $"Copied the setup for {(AgentClient.SelectedItem as AgentSetup)?.Client ?? "the agent"}.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _state.StatusText = "Clipboard is busy, try again.";
        }
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
        s.AgentPriorities = AgentPriorities.Children.OfType<CheckBox>().Where(b => b.IsChecked == true)
            .Select(b => Enum.Parse<Priority>((string)b.Tag)).ToList();

        App.ApplyTheme(s.Theme);
        _state.AppSettings.CheckForUpdates = CheckUpdates.IsChecked == true;
        _state.AppSettings.AllowAgents = AllowAgents.IsChecked == true;
        _state.AppSettings.UserName = string.IsNullOrWhiteSpace(UserName.Text) || UserName.Text.Trim() == Environment.UserName ? null : UserName.Text.Trim();
        App.Current.SetAgentAccess(_state.AppSettings.AllowAgents);
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
