using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProJict.Core.Model;

namespace ProJict.Core.Features;

public sealed record PatchNoteEntry(Category Category, TodoItem Item);

public enum VersionBump { Patch, Minor, Major }

/// <summary>Collects work finished since the last release and renders it as patch notes.</summary>
public static partial class PatchNotesBuilder
{
    [GeneratedRegex(@"(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?")]
    private static partial Regex VersionRegex();

    /// <summary>Done items not yet in a release; done children of a listed item are rendered beneath it instead.</summary>
    public static List<PatchNoteEntry> Candidates(Project project)
    {
        var result = new List<PatchNoteEntry>();
        foreach (var loc in project.Walk())
        {
            if (!IsUnreleasedDone(loc.Item)) continue;
            if (loc.Parent != null && IsUnreleasedDone(loc.Parent)) continue;
            result.Add(new PatchNoteEntry(loc.Category, loc.Item));
        }
        return result;
    }

    private static bool IsUnreleasedDone(TodoItem item) => item.IsDone && item.ReleaseId == null;

    public static string SuggestNextVersion(Project project, VersionBump bump = VersionBump.Patch)
    {
        var last = project.Releases.Where(r => !r.IsBaseline).OrderBy(r => r.Date).LastOrDefault();
        var match = last == null ? Match.Empty : VersionRegex().Match(last.Version);
        if (!match.Success) return bump == VersionBump.Major ? "1.0.0" : "0.1.0";
        var major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture);
        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture) : 0;
        var next = bump switch
        {
            VersionBump.Major => $"{major + 1}.0.0",
            VersionBump.Minor => $"{major}.{minor + 1}.0",
            _ => $"{major}.{minor}.{patch + 1}",
        };
        var prefix = last!.Version.TrimStart().StartsWith('v') ? "v" : "";
        return prefix + next;
    }

    public static string Build(Project project, string version, DateTime date, string? title,
        IEnumerable<PatchNoteEntry> entries, bool markdown)
    {
        var list = entries.ToList();
        var sb = new StringBuilder();
        var shownVersion = version.Length > 0 && char.IsDigit(version[0]) ? "v" + version : version;
        var heading = $"{project.Name} {shownVersion}".Trim();
        if (!string.IsNullOrWhiteSpace(title)) heading += $" — {title.Trim()}";
        var dateText = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (markdown) sb.Append("## ").Append(heading).Append("\n_").Append(dateText).Append("_\n");
        else sb.Append(heading).Append(" (").Append(dateText).Append(")\n");

        foreach (var category in project.Categories)
        {
            var items = list.Where(e => e.Category == category).Select(e => e.Item).ToList();
            if (items.Count == 0) continue;
            sb.Append('\n').Append(markdown ? "### " : "").Append(category.Name).Append('\n');
            foreach (var item in items) WriteItem(sb, item, 0);
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void WriteItem(StringBuilder sb, TodoItem item, int depth)
    {
        var text = item.Text.Replace("\r\n", "\n").Replace("\n", " ").Trim();
        sb.Append(new string(' ', depth * 2)).Append("- ");
        if (!string.IsNullOrWhiteSpace(item.Tag)) sb.Append(item.Tag.Trim()).Append(": ");
        sb.Append(text).Append('\n');
        foreach (var child in item.Children.Where(c => c.IsDone && c.ReleaseId == null))
            WriteItem(sb, child, depth + 1);
    }

    /// <summary>Records the release and links the included items (and their done children) to it.</summary>
    public static Release CreateRelease(Project project, string version, DateTime date, string? title,
        IEnumerable<PatchNoteEntry> entries, string markdown)
    {
        var release = new Release { Version = version.Trim(), Date = date, Title = title, Markdown = markdown };
        foreach (var entry in entries)
        {
            foreach (var item in entry.Item.Descendants().Where(c => c.IsDone && c.ReleaseId == null).Prepend(entry.Item))
            {
                item.ReleaseId = release.Id;
                release.ItemIds.Add(item.Id);
            }
        }
        project.Releases.Add(release);
        return release;
    }
}
