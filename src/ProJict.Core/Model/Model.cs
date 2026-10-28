using System.Text.Json.Serialization;

namespace ProJict.Core.Model;

public enum ItemState { Todo, InProgress, Done }

public enum ProjectStatus { Idea, Active, Paused, Released, Archived }

public sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    /// <summary>Prefix such as BUG / ADD / ISSUES, kept verbatim so exports round-trip ("BUG: text").</summary>
    public string? Tag { get; set; }
    public ItemState State { get; set; }
    public List<TodoItem> Children { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? DoneAt { get; set; }
    public Guid? ReleaseId { get; set; }
    /// <summary>True for items that came from an imported TXT file (their DoneAt is only the file date).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Imported { get; set; }

    [JsonIgnore] public bool IsDone => State == ItemState.Done;

    public void SetState(ItemState state)
    {
        if (State == state) return;
        State = state;
        DoneAt = state == ItemState.Done ? DateTime.Now : null;
        if (state != ItemState.Done) ReleaseId = null;
        // From now on the dates are real, not taken from an imported file.
        Imported = false;
    }

    public TodoItem DeepClone()
    {
        var copy = (TodoItem)MemberwiseClone();
        copy.Children = Children.Select(c => c.DeepClone()).ToList();
        return copy;
    }
}

public sealed class Category
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Text after the header, e.g. "CHARACTERS - need to more prominently feature this".</summary>
    public string? Description { get; set; }
    public bool Collapsed { get; set; }
    public List<TodoItem> Items { get; set; } = [];
}

public sealed class NoteDoc
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Note";
    public string Text { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class Release
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Version { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Now;
    public string? Title { get; set; }
    public List<Guid> ItemIds { get; set; } = [];
    public string Markdown { get; set; } = "";
    /// <summary>Marks everything done before import, so later patch notes only contain new work.</summary>
    public bool IsBaseline { get; set; }
}

public sealed class ArchivedFile
{
    public string FileName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public DateTime LastWriteTime { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
}

public sealed class Project
{
    public const string InboxKey = "Inbox";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>File-name stem used for exports, e.g. "MyGame" → TODO-MyGame.txt.</summary>
    public string Key { get; set; } = "";
    /// <summary>Prefix of the exported open-items file: TODO or WORK.</summary>
    public string FilePrefix { get; set; } = "TODO";
    public string Group { get; set; } = "Personal";
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    public bool IsInbox { get; set; }
    public string? RepoPath { get; set; }
    public string? GitHubUrl { get; set; }
    public string? PromptPreamble { get; set; }
    public string? Description { get; set; }
    public List<Category> Categories { get; set; } = [];
    public List<NoteDoc> Notes { get; set; } = [];
    public List<Release> Releases { get; set; } = [];
    public List<ArchivedFile> ArchivedFiles { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public static Project Create(string name, string? group = null) => new()
    {
        Name = name.Trim(),
        Key = KeyFromName(name),
        Group = string.IsNullOrWhiteSpace(group) ? "Personal" : group.Trim(),
    };

    public static Project CreateInbox() => new()
    {
        Name = InboxKey,
        Key = InboxKey,
        Group = "",
        IsInbox = true,
        Categories = [new Category { Name = "IDEAS" }],
    };

    /// <summary>Replaces this project's content with a snapshot's (used by undo, keeps object identity).</summary>
    public void RestoreFrom(Project snapshot)
    {
        Name = snapshot.Name;
        Key = snapshot.Key;
        FilePrefix = snapshot.FilePrefix;
        Group = snapshot.Group;
        Status = snapshot.Status;
        RepoPath = snapshot.RepoPath;
        GitHubUrl = snapshot.GitHubUrl;
        PromptPreamble = snapshot.PromptPreamble;
        Description = snapshot.Description;
        Categories = snapshot.Categories;
        Notes = snapshot.Notes;
        Releases = snapshot.Releases;
        ArchivedFiles = snapshot.ArchivedFiles;
    }

    /// <summary>Keeps letters, digits, '.', '_' and '-' so the key is a valid file name.</summary>
    public static string KeyFromName(string name)
    {
        var chars = name.Trim().Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').ToArray();
        var key = new string(chars).Trim('.');
        return key.Length == 0 ? "Project" : key;
    }
}
