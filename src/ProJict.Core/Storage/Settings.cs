namespace ProJict.Core.Storage;

/// <summary>Machine-local bootstrap settings (%LocalAppData%\ProJict\settings.json).</summary>
public sealed class AppSettings
{
    public string? DataRoot { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public Guid? LastProjectId { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Ask the GitHub CLI (gh) for its sign-in, so release checks include private repositories.</summary>
    public bool UseGitHubCliSignIn { get; set; }
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>Release tag the user chose to skip; no reminder until a newer one appears.</summary>
    public string? SkippedVersion { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProJict", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path) ? Json.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, Json.Serialize(this));
    }

    public static string DefaultDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ProJict");
}

public sealed class ExportOptions
{
    /// <summary>Write every category header even when it has no items (like the hand-made DONE files).</summary>
    public bool IncludeEmptyCategories { get; set; } = true;
    /// <summary>Also list done items as "+ ..." in the TODO file.</summary>
    public bool IncludeDoneInTodo { get; set; }
    public bool ExportNotes { get; set; }
}

/// <summary>Workspace-wide settings stored next to the data (workspace.json).</summary>
public sealed class WorkspaceSettings
{
    public const string PersonalGroup = "Personal";
    public const string WorkGroup = "Work";
    /// <summary>Not a real group: the sidebar section for archived projects.</summary>
    public const string ArchivedGroup = "Archived";

    public List<Guid> ProjectOrder { get; set; } = [];
    /// <summary>Sidebar groups in order. Personal and Work are always there; the rest can be added and removed.</summary>
    public List<string> Groups { get; set; } = [PersonalGroup, WorkGroup];
    public string? ExportFolder { get; set; }
    public ExportOptions Export { get; set; } = new();
    public bool MirrorEnabled { get; set; }
    public string? MirrorFolder { get; set; }
    /// <summary>Folders the TXT files were imported from; exporting into them would overwrite the archive.</summary>
    public List<string> ImportSourceFolders { get; set; } = [];
    public string Hotkey { get; set; } = "Ctrl+Alt+J";
    public bool CloseToTray { get; set; } = true;
    public int StaleDays { get; set; } = 30;
    public string? VsCodePath { get; set; }
    /// <summary>GitHub user or organization whose repositories are matched to projects by name.</summary>
    public string? GitHubAccount { get; set; }
    public string Theme { get; set; } = "System";
    public bool SideBySide { get; set; }
    public bool MarkCopiedInProgress { get; set; }
    public Guid? LastCaptureProjectId { get; set; }
    public string? LastCaptureCategory { get; set; }
    /// <summary>Projects left out of the sidebar list and the dashboard (shown again from either eye dropdown).</summary>
    public List<Guid> HiddenProjects { get; set; } = [];
    /// <summary>Where 0.1.2–0.1.4 kept the sidebar's hidden projects; moved into HiddenProjects on load.</summary>
    public List<Guid>? SidebarHiddenProjects { get; set; }
    /// <summary>Sidebar groups the user collapsed.</summary>
    public List<string> CollapsedGroups { get; set; } = [];

    public static bool IsDefaultGroup(string? name) =>
        string.Equals(name?.Trim(), PersonalGroup, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name?.Trim(), WorkGroup, StringComparison.OrdinalIgnoreCase);

    /// <summary>Brings settings written by older versions up to date.</summary>
    public void MigrateOldSettings()
    {
        if (SidebarHiddenProjects is { } old)
        {
            HiddenProjects = HiddenProjects.Union(old).ToList();
            SidebarHiddenProjects = null;
        }
    }

    /// <summary>Puts Personal and Work back if a hand-edited workspace.json dropped them, and drops blanks and duplicates.</summary>
    public void EnsureDefaultGroups()
    {
        Groups = Groups.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!Groups.Contains(WorkGroup, StringComparer.OrdinalIgnoreCase)) Groups.Insert(0, WorkGroup);
        if (!Groups.Contains(PersonalGroup, StringComparer.OrdinalIgnoreCase)) Groups.Insert(0, PersonalGroup);
    }

    /// <summary>The last export folder, else "ProJict-export" next to the data folder.</summary>
    public string DefaultExportFolder(string dataRoot) =>
        ExportFolder ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataRoot).TrimEnd('\\')) ?? dataRoot, "ProJict-export");
}
