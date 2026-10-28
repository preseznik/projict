using System.Text.RegularExpressions;
using ProJict.Core.Export;
using ProJict.Core.Model;

namespace ProJict.Core.Import;

public enum CategoryImportMode { List, Note, Skip }

/// <summary>Everything one target project will receive from the selected files, built for preview before importing.</summary>
public sealed class ImportDraft
{
    public required string ProjectName { get; init; }
    public string Group { get; set; } = "Personal";
    public string FilePrefix { get; set; } = "TODO";
    public bool IsInbox { get; init; }
    public List<ImportFile> Files { get; } = [];
    public Project Preview { get; set; } = new();
    public Dictionary<Guid, CategoryImportMode> CategoryModes { get; } = [];

    public int OpenCount => Preview.Walk().Count(l => l.Depth == 0 && !l.Item.IsDone);
    public int DoneCount => Preview.Walk().Count(l => l.Depth == 0 && l.Item.IsDone);
    public int NoteCount => Preview.Notes.Count;
}

public static class ImportPlanner
{
    public static List<ImportDraft> BuildDrafts(IEnumerable<ImportFile> files, Func<string, string>? readText = null)
    {
        readText ??= TextDecoder.ReadFile;
        var drafts = new List<ImportDraft>();

        var groups = files
            .Where(f => f.Role != ImportRole.Skip && !string.IsNullOrWhiteSpace(f.TargetProject))
            .GroupBy(f => f.TargetProject.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var ordered = group
                .OrderBy(f => f.Role switch { ImportRole.List => 0, ImportRole.DoneList => 1, _ => 2 })
                .ThenBy(f => !string.IsNullOrWhiteSpace(f.CategoryPrefix))
                .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var isInbox = string.Equals(group.Key, ImportScanner.InboxTarget, StringComparison.OrdinalIgnoreCase);
            var listFile = ordered.FirstOrDefault(f => f.Role == ImportRole.List);
            var draft = new ImportDraft
            {
                ProjectName = isInbox ? Project.InboxKey : group.Key,
                IsInbox = isInbox,
                Group = (listFile ?? ordered[0]).Group,
                FilePrefix = ordered.Any(f => f.Prefix == "WORK") ? "WORK" : "TODO",
            };
            draft.Files.AddRange(ordered);

            var preview = isInbox ? Project.CreateInbox() : Project.Create(draft.ProjectName, draft.Group);
            if (isInbox) preview.Categories.Clear();
            preview.FilePrefix = draft.FilePrefix;
            preview.Status = listFile == null ? ProjectStatus.Idea : ProjectStatus.Active;

            foreach (var file in ordered)
            {
                var text = readText(file.Path);
                preview.ArchivedFiles.Add(new ArchivedFile
                {
                    FileName = file.FileName,
                    SourcePath = file.Path,
                    LastWriteTime = file.LastWriteTime,
                    Text = text,
                });

                switch (file.Role)
                {
                    case ImportRole.List:
                    case ImportRole.DoneList:
                        var defaultCategory = file.Prefix == null ? file.Stem.ToUpperInvariant() : "GENERAL";
                        var doc = TxtListParser.Parse(text, defaultCategory);
                        Merge(preview, doc, file.Role == ImportRole.DoneList, file.LastWriteTime, file.CategoryPrefix, file.Stem);
                        break;
                    case ImportRole.Note:
                        preview.Notes.Add(new NoteDoc { Title = file.Stem, Text = NormalizeNewLines(text).TrimEnd() });
                        break;
                }
            }

            draft.Preview = preview;
            foreach (var category in preview.Categories) draft.CategoryModes[category.Id] = CategoryImportMode.List;
            drafts.Add(draft);
        }

        return drafts;
    }

    public static void Merge(Project target, ParsedDocument doc, bool forceDone, DateTime doneAt, string? categoryPrefix, string sourceName)
    {
        foreach (var category in doc.Categories)
        {
            var name = string.IsNullOrWhiteSpace(categoryPrefix) ? category.Name : $"{categoryPrefix.Trim()} · {category.Name}";
            var targetCategory = target.GetOrAddCategory(name);
            targetCategory.Description ??= category.Description;
            foreach (var item in category.Items)
            {
                if (forceDone) item.State = ItemState.Done;
                foreach (var x in item.Descendants().Prepend(item))
                {
                    x.Imported = true;
                    x.CreatedAt = doneAt;
                    if (x.IsDone) x.DoneAt ??= doneAt;
                }
                AddUnlessDuplicate(targetCategory, item);
            }
        }

        foreach (var note in doc.Notes)
        {
            note.Title = $"{sourceName}: {note.Title}";
            target.Notes.Add(note);
        }
    }

    /// <summary>Creates a new project (existing == null) or merges into an existing one, honoring per-category modes.</summary>
    public static Project Apply(ImportDraft draft, Project? existing, bool createBaseline)
    {
        var source = draft.Preview;
        var result = existing ?? new Project
        {
            Name = source.Name,
            Key = source.Key,
            FilePrefix = source.FilePrefix,
            Group = draft.Group,
            Status = source.Status,
            IsInbox = draft.IsInbox,
        };

        foreach (var category in source.Categories)
        {
            switch (draft.CategoryModes.GetValueOrDefault(category.Id, CategoryImportMode.List))
            {
                case CategoryImportMode.Skip:
                    break;
                case CategoryImportMode.Note:
                    result.Notes.Add(new NoteDoc { Title = category.Name, Text = TxtExporter.FormatCategory(category) });
                    break;
                default:
                    var target = result.GetOrAddCategory(category.Name);
                    target.Description ??= category.Description;
                    foreach (var item in category.Items) AddUnlessDuplicate(target, item);
                    break;
            }
        }

        foreach (var note in source.Notes)
            if (!result.Notes.Any(n => n.Title == note.Title && n.Text == note.Text))
                result.Notes.Add(note);

        foreach (var file in source.ArchivedFiles)
            if (!result.ArchivedFiles.Any(a => a.FileName == file.FileName && a.Text == file.Text))
                result.ArchivedFiles.Add(file);

        if (createBaseline)
        {
            var done = result.Walk().Select(l => l.Item).Where(i => i.IsDone && i.ReleaseId == null).ToList();
            if (done.Count > 0)
            {
                var baseline = new Release
                {
                    Version = "imported",
                    Title = "Imported baseline",
                    IsBaseline = true,
                    Date = DateTime.Now,
                    ItemIds = done.Select(i => i.Id).ToList(),
                };
                foreach (var item in done) item.ReleaseId = baseline.Id;
                result.Releases.Add(baseline);
            }
        }

        // A freshly imported project's last activity is when its files were last edited, so stale detection works at once.
        result.UpdatedAt = existing == null && result.ArchivedFiles.Count > 0
            ? result.ArchivedFiles.Max(f => f.LastWriteTime)
            : DateTime.Now;
        return result;
    }

    private static void AddUnlessDuplicate(Category category, TodoItem item)
    {
        var key = TextKey(item);
        if (category.Items.Any(e => e.State == item.State && TextKey(e) == key)) return;
        category.Items.Add(item);
    }

    private static string TextKey(TodoItem item) =>
        Regex.Replace($"{item.Tag}:{item.Text}", @"\s+", " ").Trim().ToLowerInvariant();

    private static string NormalizeNewLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
}
