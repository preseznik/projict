using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.Core.Agents;

public sealed record FolderMatch(Project Project, string How);

/// <summary>Finds the project an agent is working on from its working folder.</summary>
public static class ProjectMatcher
{
    /// <summary>
    /// In order: a project whose local repo folder is the folder or one of its parents (the deepest wins); a project
    /// whose GitHub link is a remote of the folder's git repository; a project named like the repository folder.
    /// </summary>
    public static FolderMatch? ForFolder(IEnumerable<Project> projects, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var list = projects.Where(p => !p.IsInbox).ToList();
        var full = Normalize(folder);

        var byRepo = list
            .Where(p => !string.IsNullOrWhiteSpace(p.RepoPath))
            .Select(p => (Project: p, Root: Normalize(p.RepoPath!)))
            .Where(x => full.Equals(x.Root, StringComparison.OrdinalIgnoreCase) ||
                        full.StartsWith(x.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Root.Length)
            .FirstOrDefault();
        if (byRepo.Project != null) return new FolderMatch(byRepo.Project, $"its repo folder {byRepo.Project.RepoPath}");

        var gitRoot = GitRoot(full);
        if (gitRoot != null)
        {
            foreach (var remote in GitRemotes(gitRoot))
            {
                if (GitHubReleases.ParseRepo(remote) is not { } repo) continue;
                var byRemote = list.FirstOrDefault(p => GitHubReleases.ParseRepo(p.GitHubUrl) is { } own &&
                    own.Owner.Equals(repo.Owner, StringComparison.OrdinalIgnoreCase) && own.Name.Equals(repo.Name, StringComparison.OrdinalIgnoreCase));
                if (byRemote != null) return new FolderMatch(byRemote, $"the git remote {remote}");
            }
        }

        var folderKey = GitHubReleases.MatchKey(Path.GetFileName(gitRoot ?? full));
        var byName = folderKey.Length == 0 ? null : list.FirstOrDefault(p =>
            GitHubReleases.MatchKey(p.Key) == folderKey || GitHubReleases.MatchKey(p.Name) == folderKey);
        return byName == null ? null : new FolderMatch(byName, $"the folder name {Path.GetFileName(gitRoot ?? full)}");
    }

    private static string Normalize(string path) => Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The folder holding .git, from the folder upwards.</summary>
    private static string? GitRoot(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        return null;
    }

    private static IEnumerable<string> GitRemotes(string gitRoot)
    {
        var config = GitConfigPath(gitRoot);
        if (config == null) yield break;
        string[] lines;
        try { lines = File.ReadAllLines(config); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (var line in lines.Select(l => l.Trim()))
            if (line.StartsWith("url", StringComparison.OrdinalIgnoreCase) && line.IndexOf('=') is > 0 and var eq)
                yield return line[(eq + 1)..].Trim();
    }

    /// <summary>.git/config; for a worktree or submodule (.git is a file) the repository's shared config.</summary>
    private static string? GitConfigPath(string gitRoot)
    {
        var dotGit = Path.Combine(gitRoot, ".git");
        if (Directory.Exists(dotGit)) return File.Exists(Path.Combine(dotGit, "config")) ? Path.Combine(dotGit, "config") : null;
        try
        {
            var pointer = File.ReadAllText(dotGit).Trim();
            if (!pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return null;
            var gitDir = Path.GetFullPath(Path.Combine(gitRoot, pointer["gitdir:".Length..].Trim()));
            var common = Path.Combine(gitDir, "commondir");
            if (File.Exists(common)) gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(common).Trim()));
            var config = Path.Combine(gitDir, "config");
            return File.Exists(config) ? config : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
