using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ProJict.Core.Export;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.App;

/// <summary>Owns the loaded workspace and persists it: debounced autosave, periodic backups and the optional TXT mirror.</summary>
public sealed partial class AppState : ObservableObject
{
    private static readonly TimeSpan BackupInterval = TimeSpan.FromMinutes(30);
    private readonly HashSet<Project> _dirty = [];
    private readonly HashSet<Project> _mirrorPending = [];
    private readonly DispatcherTimer _saveTimer;
    private readonly bool _persistAppSettings;
    private bool _settingsDirty;
    private DateTime _lastBackup = DateTime.MinValue;

    public AppState(ProjectStore store, AppSettings appSettings, bool persistAppSettings)
    {
        Store = store;
        AppSettings = appSettings;
        _persistAppSettings = persistAppSettings;
        Workspace = store.Load();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    public ProjectStore Store { get; }
    public AppSettings AppSettings { get; }
    public Workspace Workspace { get; }
    public WorkspaceSettings Settings => Workspace.Settings;
    public IReadOnlyList<Project> Projects => Workspace.Projects;
    public Project Inbox => Workspace.Inbox;

    [ObservableProperty] private string _statusText = "";

    /// <summary>Raised after any edit to a project (sidebar counts, dashboard).</summary>
    public event Action<Project>? ProjectChanged;

    /// <summary>Raised when projects are added/removed or renamed/regrouped.</summary>
    public event Action? ProjectListChanged;

    /// <summary>Raised when a project was changed outside its open view (quick capture, import), so views rebuild.</summary>
    public event Action<Project>? ProjectReplaced;

    public void MarkDirty(Project project, bool touch = true)
    {
        if (touch) project.UpdatedAt = DateTime.Now;
        _dirty.Add(project);
        RestartTimer();
        ProjectChanged?.Invoke(project);
    }

    public void MarkSettingsDirty()
    {
        _settingsDirty = true;
        RestartTimer();
    }

    public void NotifyListChanged() => ProjectListChanged?.Invoke();

    public void NotifyReplaced(Project project)
    {
        MarkDirty(project);
        ProjectReplaced?.Invoke(project);
    }

    public void AddProject(Project project)
    {
        Workspace.Projects.Add(project);
        Settings.ProjectOrder.Add(project.Id);
        if (!string.IsNullOrWhiteSpace(project.Group) && !project.IsInbox &&
            !Settings.Groups.Contains(project.Group, StringComparer.OrdinalIgnoreCase))
            Settings.Groups.Add(project.Group);
        MarkSettingsDirty();
        MarkDirty(project, touch: false);
        NotifyListChanged();
    }

    public void DeleteProject(Project project)
    {
        if (project.IsInbox) return;
        Workspace.Projects.Remove(project);
        Settings.ProjectOrder.Remove(project.Id);
        _dirty.Remove(project);
        _mirrorPending.Remove(project);
        try
        {
            Store.Delete(project);
            if (Settings.MirrorEnabled && !string.IsNullOrWhiteSpace(Settings.MirrorFolder))
                ExportService.RemoveProjectFiles(Settings.MirrorFolder, project.Id);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not remove project file: {ex.Message}";
        }
        MarkSettingsDirty();
        NotifyListChanged();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        var failed = new List<string>();
        foreach (var project in _dirty.ToList())
        {
            try
            {
                Store.Save(project);
                _dirty.Remove(project);
                _mirrorPending.Add(project);
            }
            catch (Exception ex)
            {
                failed.Add($"{project.Name}: {ex.Message}");
            }
        }

        if (_settingsDirty)
        {
            try
            {
                Store.SaveSettings(Settings);
                _settingsDirty = false;
            }
            catch (Exception ex)
            {
                failed.Add($"workspace.json: {ex.Message}");
            }
        }

        if (failed.Count > 0)
        {
            StatusText = "Save failed — " + string.Join("; ", failed);
            RestartTimer();
            return;
        }

        var mirrorNote = RunMirror();
        MaybeBackup();
        StatusText = $"Saved {DateTime.Now:HH:mm:ss}{mirrorNote}";
    }

    public void SaveAppSettings()
    {
        if (!_persistAppSettings) return;
        try { AppSettings.Save(); }
        catch (Exception) { /* window placement is not worth an error dialog */ }
    }

    private string RunMirror()
    {
        if (_mirrorPending.Count == 0) return "";
        if (!Settings.MirrorEnabled || string.IsNullOrWhiteSpace(Settings.MirrorFolder))
        {
            _mirrorPending.Clear();
            return "";
        }
        try
        {
            var result = ExportService.Export(_mirrorPending.ToList(), Settings.MirrorFolder, Settings.Export, overwriteConflicts: false);
            _mirrorPending.Clear();
            if (result.Conflicts.Count > 0)
                return $" · mirror skipped {result.Conflicts.Count} file(s) not written by ProJict: {string.Join(", ", result.Conflicts)}";
            if (result.Errors.Count > 0) return " · mirror error: " + result.Errors[0];
            return result.Written.Count > 0 ? $" · mirrored {result.Written.Count} file(s)" : "";
        }
        catch (Exception ex)
        {
            return " · mirror error: " + ex.Message;
        }
    }

    public void BackupNow()
    {
        try
        {
            Store.Backup();
            _lastBackup = DateTime.Now;
        }
        catch (Exception ex)
        {
            StatusText = "Backup failed: " + ex.Message;
        }
    }

    private void MaybeBackup()
    {
        if (DateTime.Now - _lastBackup >= BackupInterval) BackupNow();
    }

    private void RestartTimer()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
