using System.Text.RegularExpressions;

namespace ProJict.Core.Import;

public enum ImportRole { Skip, List, DoneList, Note }

public sealed class ImportFile
{
    public required string Path { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Stem => System.IO.Path.GetFileNameWithoutExtension(Path);
    /// <summary>Normalized prefix (TODO, TODO2, DONE, WORK, XBOX, …) or null for files without one.</summary>
    public string? Prefix { get; init; }
    /// <summary>The part after the prefix, e.g. "MyGame" for TODO-MyGame.txt.</summary>
    public string Key { get; init; } = "";
    public long Size { get; init; }
    public DateTime LastWriteTime { get; init; }
    public ImportRole Role { get; set; }
    public string TargetProject { get; set; } = "";
    public string Group { get; set; } = "Personal";
    /// <summary>Optional prefix for merged categories, e.g. "MAPS" turns "HUD" into "MAPS · HUD".</summary>
    public string? CategoryPrefix { get; set; }
}

public static partial class ImportScanner
{
    public const string InboxTarget = "Inbox";

    [GeneratedRegex(@"^(?<prefix>TODO\d*|DONE|WORK|XBOX|DOCS|IMPORTANT|MANUAL|BIG)-(?<key>.+)\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixRegex();

    private static readonly string[] InboxFiles = ["PLAN", "IDEJE", "IDEAS"];

    /// <summary>Lists the folder's top-level *.txt files and suggests a role and target project for each. Reads metadata only.</summary>
    public static List<ImportFile> Scan(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*.txt", SearchOption.TopDirectoryOnly)
            .OrderBy(p => System.IO.Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return Classify(path, info.Length, info.LastWriteTime);
            })
            .ToList();
        SuggestTargets(files);
        return files;
    }

    internal static ImportFile Classify(string path, long size, DateTime lastWrite)
    {
        var name = System.IO.Path.GetFileName(path);
        var match = PrefixRegex().Match(name);
        if (!match.Success)
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(path);
            var isInbox = InboxFiles.Contains(stem, StringComparer.OrdinalIgnoreCase);
            return new ImportFile
            {
                Path = path, Size = size, LastWriteTime = lastWrite, Key = stem,
                // Unknown files (credentials, billing, …) are never imported unless the user opts in.
                Role = isInbox ? ImportRole.List : ImportRole.Skip,
                TargetProject = isInbox ? InboxTarget : "",
            };
        }

        var prefix = match.Groups["prefix"].Value.ToUpperInvariant();
        var role = prefix.StartsWith("TODO") || prefix == "WORK" ? ImportRole.List
            : prefix == "DONE" ? ImportRole.DoneList
            : ImportRole.Note;
        return new ImportFile
        {
            Path = path, Size = size, LastWriteTime = lastWrite,
            Prefix = prefix, Key = match.Groups["key"].Value, Role = role,
            Group = prefix == "WORK" ? "Work" : "Personal",
        };
    }

    internal static void SuggestTargets(List<ImportFile> files)
    {
        // Projects are defined by their open-item lists (TODO-*, WORK-*).
        var primaries = new List<ImportFile>();
        foreach (var f in files.Where(f => f.Prefix != null && f.Role == ImportRole.List))
            if (!primaries.Any(p => Normalize(p.Key) == Normalize(f.Key)))
                primaries.Add(f);

        foreach (var file in files.Where(f => f.Prefix != null))
        {
            // TODO-MyGameMAPS → MyGame with categories prefixed "MAPS".
            var parent = primaries
                .Where(p => p != file && file.Key.Length - p.Key.Length >= 2 &&
                            file.Key.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Key.Length)
                .FirstOrDefault();
            var exact = primaries.FirstOrDefault(p => Normalize(p.Key) == Normalize(file.Key));

            if (exact != null && (exact != file || parent == null))
            {
                Assign(file, exact, null);
                continue;
            }
            if (parent != null)
            {
                var remainder = file.Key[parent.Key.Length..].Trim('-', '_', ' ', '.');
                Assign(file, parent, file.Role == ImportRole.List && remainder.Length > 0 ? remainder : null);
                continue;
            }

            if (file.Role != ImportRole.List)
            {
                // DONE-MyAppVR2 ↔ TODO-MyApp2VR: same letters or a small typo.
                var fuzzy = primaries
                    .Where(p => IsNearMatch(p.Key, file.Key))
                    .OrderBy(p => EditDistance(Normalize(p.Key), Normalize(file.Key)))
                    .FirstOrDefault();
                if (fuzzy != null)
                {
                    Assign(file, fuzzy, null);
                    continue;
                }
            }

            file.TargetProject = file.Key;
        }
    }

    private static void Assign(ImportFile file, ImportFile primary, string? categoryPrefix)
    {
        file.TargetProject = primary.Key;
        file.Group = primary.Group;
        file.CategoryPrefix = categoryPrefix;
    }

    public static string Normalize(string key) =>
        new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static bool IsNearMatch(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length < 5 || nb.Length < 5) return false;
        if (na.Length == nb.Length && na.Order().SequenceEqual(nb.Order())) return true;
        // Short keys like "1v1me" differ meaningfully by one character, so typos only count on longer ones.
        return Math.Min(na.Length, nb.Length) >= 8 && EditDistance(na, nb) <= 2;
    }

    internal static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
