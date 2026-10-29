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
    /// <summary>Files attached to the item; null (and left out of the JSON) while there are none.</summary>
    public List<Attachment>? Attachments { get; set; }
    /// <summary>The AI agent working on the item while it is in progress; any state change clears it.</summary>
    public string? ClaimedBy { get; set; }
    /// <summary>Finished by an AI agent and not checked by you yet; any state change clears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NeedsReview { get; set; }
    /// <summary>What happened to the item (a claim, a resolution, a review), signed by who did it; null while empty.</summary>
    public List<ActivityEntry>? Activity { get; set; }
    /// <summary>Discussion threads about the item; null while there are none.</summary>
    public List<Comment>? Comments { get; set; }

    [JsonIgnore] public bool IsDone => State == ItemState.Done;
    [JsonIgnore] public bool HasAttachments => Attachments is { Count: > 0 };

    public void SetState(ItemState state)
    {
        if (State == state) return;
        State = state;
        DoneAt = state == ItemState.Done ? DateTime.Now : null;
        if (state != ItemState.Done) ReleaseId = null;
        ClaimedBy = null;
        NeedsReview = false;
        // From now on the dates are real, not taken from an imported file.
        Imported = false;
    }

    public TodoItem DeepClone()
    {
        var copy = (TodoItem)MemberwiseClone();
        copy.Children = Children.Select(c => c.DeepClone()).ToList();
        copy.Attachments = Attachments?.ToList();
        copy.Activity = Activity?.ToList();
        copy.Comments = Comments?.Select(c => c.DeepClone()).ToList();
        return copy;
    }

    public void AddActivity(string by, string text, bool agent) =>
        (Activity ??= []).Add(new ActivityEntry { By = by, Text = text, Agent = agent });
}

/// <summary>One line in an item's history, e.g. an agent's resolution: "Fixed in 615d691".</summary>
public sealed class ActivityEntry
{
    public DateTime At { get; set; } = DateTime.Now;
    public string By { get; set; } = "";
    /// <summary>Written by an AI agent rather than a person.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Agent { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>A message in a discussion; replies nest to any depth (sub-threads).</summary>
public sealed class Comment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string By { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Agent { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
    /// <summary>Removed while it had replies: the text is gone, the thread below it stays.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Deleted { get; set; }
    public List<Comment>? Replies { get; set; }

    public Comment DeepClone()
    {
        var copy = (Comment)MemberwiseClone();
        copy.Replies = Replies?.Select(r => r.DeepClone()).ToList();
        return copy;
    }
}

/// <summary>A file attached to an item. ProJict keeps its own copy in the data folder (see ProjectStore.AttachmentPath).</summary>
public sealed class Attachment
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp" };

    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;

    [JsonIgnore] public bool IsImage => ImageExtensions.Contains(Path.GetExtension(FileName));
}

public sealed class Category
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Text after the header, e.g. "CHARACTERS - need to more prominently feature this".</summary>
    public string? Description { get; set; }
    public bool Collapsed { get; set; }
    public List<TodoItem> Items { get; set; } = [];
    /// <summary>Discussion threads about the category; null while there are none.</summary>
    public List<Comment>? Comments { get; set; }
}

public sealed class NoteDoc
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Note";
    public string Text { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    /// <summary>Who last changed the note: you or an AI agent.</summary>
    public string? UpdatedBy { get; set; }
    /// <summary>Files that go with the note (charts, PDFs, screenshots); null while there are none.</summary>
    public List<Attachment>? Attachments { get; set; }
}

public sealed class Release
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Version { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Now;
    public string? Title { get; set; }
    public List<Guid> ItemIds { get; set; } = [];
    public string Markdown { get; set; } = "";
    /// <summary>Who recorded the release: you or an AI agent.</summary>
    public string? CreatedBy { get; set; }
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

/// <summary>The newest published release of a project's GitHub repository, as last checked.</summary>
public sealed class GitHubReleaseInfo
{
    /// <summary>The repository link that was checked; a different link means the info is out of date.</summary>
    public string Repository { get; set; } = "";
    /// <summary>Tag of the newest published release; null when the repository has none.</summary>
    public string? Tag { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? Url { get; set; }
    /// <summary>Only set when the repository has nothing but pre-releases.</summary>
    public bool Prerelease { get; set; }
    public DateTime CheckedAt { get; set; }
    /// <summary>Why the check failed (e.g. a private repository without sign-in); null when it worked.</summary>
    public string? Error { get; set; }
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
    /// <summary>What GitHub said about the project's repository the last time ProJict asked (not part of undo).</summary>
    public GitHubReleaseInfo? GitHubRelease { get; set; }
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
