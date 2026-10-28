using System.Text;
using ProJict.Core.Import;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.Core.Export;

/// <summary>Writes projects back into the hand-written TXT format (CRLF, tab indents, "- " / "-+ " / "+ " markers).</summary>
public static class TxtExporter
{
    public const string NewLine = "\r\n";

    public static string TodoFileName(Project p) => $"{SafePrefix(p)}-{SafeKey(p)}.txt";
    public static string DoneFileName(Project p) => $"DONE-{SafeKey(p)}.txt";
    public static string NotesFileName(Project p) => $"NOTES-{SafeKey(p)}.txt";

    public static string ExportTodo(Project project, ExportOptions options) =>
        Write(project, options, items => items.Where(i => !i.IsDone || options.IncludeDoneInTodo));

    public static string ExportDone(Project project, ExportOptions options) =>
        // Oldest first, like lines appended to a DONE file over time; OrderBy is stable for equal dates.
        Write(project, options, items => items.Where(i => i.IsDone).OrderBy(i => i.DoneAt ?? DateTime.MinValue));

    public static string ExportNotes(Project project)
    {
        var sb = new StringBuilder();
        foreach (var note in project.Notes)
        {
            if (sb.Length > 0) sb.Append(NewLine);
            sb.Append("### ").Append(note.Title).Append(" ###").Append(NewLine);
            foreach (var line in SplitLines(note.Text)) sb.Append(line).Append(NewLine);
        }
        return sb.ToString();
    }

    /// <summary>One category with open items first, then done items (used when a category is turned into a note).</summary>
    public static string FormatCategory(Category category)
    {
        var sb = new StringBuilder();
        WriteHeader(sb, category);
        foreach (var item in category.Items.Where(i => !i.IsDone)) WriteItem(sb, item, 0);
        foreach (var item in category.Items.Where(i => i.IsDone)) WriteItem(sb, item, 0);
        return sb.ToString().TrimEnd();
    }

    public static string FormatItems(IEnumerable<TodoItem> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items) WriteItem(sb, item, 0);
        return sb.ToString();
    }

    private static string Write(Project project, ExportOptions options, Func<IEnumerable<TodoItem>, IEnumerable<TodoItem>> select)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var category in project.Categories)
        {
            var items = select(category.Items).ToList();
            if (items.Count == 0 && !options.IncludeEmptyCategories) continue;
            if (!first) sb.Append(NewLine);
            first = false;
            WriteHeader(sb, category);
            foreach (var item in items) WriteItem(sb, item, 0);
        }
        return sb.ToString();
    }

    private static void WriteHeader(StringBuilder sb, Category category)
    {
        var name = category.Name.Trim();
        // Headers the parser would not recognize on their own get a trailing colon ("look into:").
        if (!TxtListParser.TryHeader(name, strongOnly: true, out _, out _)) name = name.TrimEnd(':') + ":";
        sb.Append(name);
        if (!string.IsNullOrWhiteSpace(category.Description))
        {
            if (name.EndsWith(':')) sb.Length--;
            sb.Append(" - ").Append(category.Description.Trim());
        }
        sb.Append(NewLine);
    }

    private static void WriteItem(StringBuilder sb, TodoItem item, int depth)
    {
        var lines = SplitLines(item.Text.Replace('\t', ' '));
        sb.Append('\t', depth).Append(Marker(item.State)).Append(' ');
        if (!string.IsNullOrWhiteSpace(item.Tag)) sb.Append(item.Tag.Trim()).Append(": ");
        sb.Append(lines[0].Trim()).Append(NewLine);
        foreach (var line in lines.Skip(1).Where(l => l.Trim().Length > 0))
            sb.Append('\t', depth + 1).Append(line.Trim()).Append(NewLine);
        foreach (var child in item.Children) WriteItem(sb, child, depth + 1);
    }

    public static string Marker(ItemState state) => state switch
    {
        ItemState.Done => "+",
        ItemState.InProgress => "-+",
        _ => "-",
    };

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string SafeKey(Project p) => SafeFileStem(string.IsNullOrWhiteSpace(p.Key) ? p.Name : p.Key);

    private static string SafePrefix(Project p) => SafeFileStem(string.IsNullOrWhiteSpace(p.FilePrefix) ? "TODO" : p.FilePrefix.Trim());

    private static string SafeFileStem(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.');
        return cleaned.Length == 0 ? "Project" : cleaned;
    }
}
