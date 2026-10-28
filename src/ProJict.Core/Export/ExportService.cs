using System.Security.Cryptography;
using System.Text;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.Core.Export;

public sealed record ExportFile(string FileName, string Content, Guid ProjectId);

public sealed class ExportResult
{
    public List<string> Written { get; } = [];
    public List<string> Unchanged { get; } = [];
    /// <summary>Existing files ProJict did not write (or that were edited since) and were therefore left alone.</summary>
    public List<string> Conflicts { get; } = [];
    public List<string> Removed { get; } = [];
    public List<string> Errors { get; } = [];
}

public sealed class ExportManifest
{
    public Dictionary<string, ManifestEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ManifestEntry
{
    public Guid ProjectId { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime WrittenAt { get; set; }
}

/// <summary>
/// Exports projects into a folder and tracks what it wrote in .projict-export.json,
/// so it only ever overwrites or removes its own, unmodified files.
/// </summary>
public static class ExportService
{
    public const string ManifestName = ".projict-export.json";
    private static readonly UTF8Encoding Utf8 = new(false);

    public static List<ExportFile> BuildFiles(Project project, ExportOptions options)
    {
        var files = new List<ExportFile>
        {
            new(TxtExporter.TodoFileName(project), TxtExporter.ExportTodo(project, options), project.Id),
        };
        var hasDone = project.Categories.Any(c => c.Items.Any(i => i.IsDone));
        if (hasDone || (options.IncludeEmptyCategories && project.Categories.Count > 0))
            files.Add(new(TxtExporter.DoneFileName(project), TxtExporter.ExportDone(project, options), project.Id));
        if (options.ExportNotes && project.Notes.Count > 0)
            files.Add(new(TxtExporter.NotesFileName(project), TxtExporter.ExportNotes(project), project.Id));
        return files;
    }

    public static ExportResult Export(IEnumerable<Project> projects, string folder, ExportOptions options, bool overwriteConflicts)
    {
        var result = new ExportResult();
        Directory.CreateDirectory(folder);
        var manifest = LoadManifest(folder);

        foreach (var project in projects)
        {
            var files = BuildFiles(project, options);
            foreach (var file in files)
            {
                try
                {
                    WriteFile(folder, file, manifest, overwriteConflicts, result);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Errors.Add($"{file.FileName}: {ex.Message}");
                }
            }

            // Files this project wrote under an older name (project renamed) are cleaned up.
            var current = files.Select(f => f.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = manifest.Files.Where(kv => kv.Value.ProjectId == project.Id && !current.Contains(kv.Key))
                .Select(kv => kv.Key).ToList();
            RemoveOwned(folder, manifest, stale, result);
        }

        SaveManifest(folder, manifest, result);
        return result;
    }

    /// <summary>Removes a deleted project's files, but only those still exactly as ProJict wrote them.</summary>
    public static ExportResult RemoveProjectFiles(string folder, Guid projectId)
    {
        var result = new ExportResult();
        if (!Directory.Exists(folder)) return result;
        var manifest = LoadManifest(folder);
        var owned = manifest.Files.Where(kv => kv.Value.ProjectId == projectId).Select(kv => kv.Key).ToList();
        RemoveOwned(folder, manifest, owned, result);
        SaveManifest(folder, manifest, result);
        return result;
    }

    private static void WriteFile(string folder, ExportFile file, ExportManifest manifest, bool overwriteConflicts, ExportResult result)
    {
        var path = Path.Combine(folder, file.FileName);
        var bytes = Utf8.GetBytes(file.Content);
        var hash = Hash(bytes);

        if (File.Exists(path))
        {
            var existingHash = Hash(File.ReadAllBytes(path));
            if (existingHash == hash)
            {
                manifest.Files[file.FileName] = new ManifestEntry { ProjectId = file.ProjectId, Sha256 = hash, WrittenAt = DateTime.Now };
                result.Unchanged.Add(file.FileName);
                return;
            }
            var owned = manifest.Files.TryGetValue(file.FileName, out var entry) && entry.Sha256 == existingHash;
            if (!owned && !overwriteConflicts)
            {
                result.Conflicts.Add(file.FileName);
                return;
            }
        }

        AtomicFile.WriteAllText(path, file.Content);
        manifest.Files[file.FileName] = new ManifestEntry { ProjectId = file.ProjectId, Sha256 = hash, WrittenAt = DateTime.Now };
        result.Written.Add(file.FileName);
    }

    private static void RemoveOwned(string folder, ExportManifest manifest, List<string> names, ExportResult result)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            try
            {
                if (File.Exists(path))
                {
                    if (Hash(File.ReadAllBytes(path)) != manifest.Files[name].Sha256)
                    {
                        // Edited by hand since export: keep the file, forget ownership.
                        manifest.Files.Remove(name);
                        continue;
                    }
                    File.Delete(path);
                    result.Removed.Add(name);
                }
                manifest.Files.Remove(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Errors.Add($"{name}: {ex.Message}");
            }
        }
    }

    public static ExportManifest LoadManifest(string folder)
    {
        var path = Path.Combine(folder, ManifestName);
        try
        {
            if (File.Exists(path))
            {
                var manifest = Json.Deserialize<ExportManifest>(File.ReadAllText(path));
                if (manifest != null)
                {
                    manifest.Files = new Dictionary<string, ManifestEntry>(manifest.Files, StringComparer.OrdinalIgnoreCase);
                    return manifest;
                }
            }
        }
        catch (Exception) { }
        return new ExportManifest();
    }

    private static void SaveManifest(string folder, ExportManifest manifest, ExportResult result)
    {
        try
        {
            AtomicFile.WriteAllText(Path.Combine(folder, ManifestName), Json.Serialize(manifest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Errors.Add($"{ManifestName}: {ex.Message}");
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
