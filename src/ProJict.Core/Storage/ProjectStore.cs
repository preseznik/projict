using System.Globalization;
using ProJict.Core.Model;

namespace ProJict.Core.Storage;

public sealed class Workspace
{
    public WorkspaceSettings Settings { get; init; } = new();
    public List<Project> Projects { get; init; } = [];
    public Project Inbox => Projects.First(p => p.IsInbox);
}

/// <summary>
/// One JSON file per project under &lt;dataRoot&gt;/projects, workspace.json for settings,
/// timestamped copies under &lt;dataRoot&gt;/backups.
/// </summary>
public sealed class ProjectStore
{
    private const string BackupStampFormat = "yyyyMMdd-HHmmss";
    private readonly Dictionary<Guid, string> _fileNames = [];
    private readonly List<string> _loadErrors = [];

    public ProjectStore(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string DataRoot { get; }
    public string ProjectsDir => Path.Combine(DataRoot, "projects");
    public string BackupsDir => Path.Combine(DataRoot, "backups");
    public string WorkspacePath => Path.Combine(DataRoot, "workspace.json");
    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public Workspace Load()
    {
        Directory.CreateDirectory(ProjectsDir);
        _fileNames.Clear();
        _loadErrors.Clear();

        var settings = new WorkspaceSettings();
        if (File.Exists(WorkspacePath))
        {
            try { settings = Json.Deserialize<WorkspaceSettings>(File.ReadAllText(WorkspacePath)) ?? new(); }
            catch (Exception ex) { _loadErrors.Add($"workspace.json: {ex.Message}"); }
        }

        var projects = new List<Project>();
        foreach (var file in Directory.EnumerateFiles(ProjectsDir, "*.json"))
        {
            try
            {
                var project = Json.Deserialize<Project>(File.ReadAllText(file));
                if (project == null) continue;
                if (_fileNames.ContainsKey(project.Id))
                {
                    _loadErrors.Add($"{Path.GetFileName(file)}: duplicate project id, skipped");
                    continue;
                }
                _fileNames[project.Id] = Path.GetFileName(file);
                projects.Add(project);
            }
            catch (Exception ex)
            {
                // Leave the broken file in place untouched; it is never overwritten because its id is unknown.
                _loadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (!projects.Any(p => p.IsInbox))
        {
            var inbox = Project.CreateInbox();
            projects.Add(inbox);
            Save(inbox);
        }

        var order = settings.ProjectOrder;
        projects = projects
            .OrderBy(p => p.IsInbox ? -1 : order.IndexOf(p.Id) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Workspace { Settings = settings, Projects = projects };
    }

    public void Save(Project project)
    {
        Directory.CreateDirectory(ProjectsDir);
        var desired = UniqueFileName(project);
        AtomicFile.WriteAllText(Path.Combine(ProjectsDir, desired), Json.Serialize(project));
        if (_fileNames.TryGetValue(project.Id, out var old) &&
            !string.Equals(old, desired, StringComparison.OrdinalIgnoreCase))
        {
            var oldPath = Path.Combine(ProjectsDir, old);
            if (File.Exists(oldPath)) File.Delete(oldPath);
        }
        _fileNames[project.Id] = desired;
    }

    public void SaveSettings(WorkspaceSettings settings)
    {
        Directory.CreateDirectory(DataRoot);
        AtomicFile.WriteAllText(WorkspacePath, Json.Serialize(settings));
    }

    /// <summary>Moves the project's file into backups/deleted instead of erasing it.</summary>
    public void Delete(Project project)
    {
        if (!_fileNames.Remove(project.Id, out var name)) return;
        var path = Path.Combine(ProjectsDir, name);
        if (!File.Exists(path)) return;
        var deletedDir = Path.Combine(BackupsDir, "deleted");
        Directory.CreateDirectory(deletedDir);
        File.Move(path, Path.Combine(deletedDir, $"{DateTime.Now.ToString(BackupStampFormat, CultureInfo.InvariantCulture)}-{name}"), overwrite: true);
    }

    public string Backup(DateTime? now = null)
    {
        var stamp = (now ?? DateTime.Now).ToString(BackupStampFormat, CultureInfo.InvariantCulture);
        var dir = Path.Combine(BackupsDir, stamp);
        for (var i = 2; Directory.Exists(dir); i++) dir = Path.Combine(BackupsDir, $"{stamp}-{i}");
        var projectsBackup = Path.Combine(dir, "projects");
        Directory.CreateDirectory(projectsBackup);
        if (Directory.Exists(ProjectsDir))
            foreach (var file in Directory.EnumerateFiles(ProjectsDir, "*.json"))
                File.Copy(file, Path.Combine(projectsBackup, Path.GetFileName(file)));
        if (File.Exists(WorkspacePath))
            File.Copy(WorkspacePath, Path.Combine(dir, "workspace.json"));
        Prune(now ?? DateTime.Now);
        return dir;
    }

    /// <summary>Keeps the newest <paramref name="keepLatest"/> backups plus the newest one of each day for <paramref name="keepDays"/> days.</summary>
    public void Prune(DateTime now, int keepLatest = 20, int keepDays = 30)
    {
        if (!Directory.Exists(BackupsDir)) return;
        var backups = Directory.GetDirectories(BackupsDir)
            .Select(d => (Path: d, Stamp: ParseStamp(Path.GetFileName(d))))
            .Where(b => b.Stamp != null)
            .OrderByDescending(b => b.Stamp)
            .ThenByDescending(b => b.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var keep = new HashSet<string>(backups.Take(keepLatest).Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var day in backups.Where(b => b.Stamp!.Value >= now.Date.AddDays(-keepDays)).GroupBy(b => b.Stamp!.Value.Date))
            keep.Add(day.First().Path);

        foreach (var backup in backups.Where(b => !keep.Contains(b.Path)))
        {
            try { Directory.Delete(backup.Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static DateTime? ParseStamp(string name)
    {
        if (name.Length < BackupStampFormat.Length) return null;
        return DateTime.TryParseExact(name[..BackupStampFormat.Length], BackupStampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp) ? stamp : null;
    }

    private string UniqueFileName(Project project)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string(project.Key.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (stem.Length == 0) stem = "project";
        var candidate = stem + ".json";
        for (var i = 2; _fileNames.Any(kv => kv.Key != project.Id &&
                 string.Equals(kv.Value, candidate, StringComparison.OrdinalIgnoreCase)); i++)
            candidate = $"{stem}-{i}.json";
        return candidate;
    }
}
