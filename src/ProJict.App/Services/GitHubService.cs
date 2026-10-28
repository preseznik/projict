using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>Links projects to GitHub repositories by name and keeps each one's newest published release.</summary>
public sealed class GitHubService(AppState state)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    /// <summary>Automatic checks skip repositories looked at more recently than this (GitHub allows 60 requests an hour without sign-in).</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
    private bool _running;

    /// <summary>Raised after projects got a GitHub link or new release info.</summary>
    public event Action? Updated;

    /// <summary>True when there is anything to check: an account, the CLI sign-in or a project with a GitHub link.</summary>
    public bool HasWork =>
        !string.IsNullOrWhiteSpace(state.Settings.GitHubAccount) || state.AppSettings.UseGitHubCliSignIn ||
        state.Projects.Any(p => GitHubReleases.ParseRepo(p.GitHubUrl) != null);

    /// <summary>Links unlinked projects, then checks the releases of linked ones (only stale ones unless forced). Returns a summary.</summary>
    public async Task<string> RefreshAsync(bool force)
    {
        if (_running) return "Already checking GitHub…";
        if (!HasWork) return "Enter your GitHub account (or turn on the GitHub CLI sign-in) so ProJict can find your projects' repositories.";
        _running = true;
        var notes = new List<string>();
        var changed = false;
        try
        {
            string? token = null;
            if (state.AppSettings.UseGitHubCliSignIn)
            {
                token = await Task.Run(GitHubCliToken);
                if (token == null) notes.Add("The GitHub CLI sign-in wasn't found (is gh installed and signed in?), so only public repositories were checked.");
            }

            var repos = await GitHubReleases.ListReposAsync(Http, state.Settings.GitHubAccount, token);
            var linked = GitHubReleases.MatchUnlinked(state.Projects, repos);
            foreach (var (project, repo) in linked)
            {
                project.GitHubUrl = repo.Url;
                state.MarkDirty(project, touch: false);
            }
            changed |= linked.Count > 0;

            var released = new List<string>();
            var failed = new List<string>();
            var checkedCount = 0;
            foreach (var project in state.Projects.Where(p => !p.IsInbox).ToList())
            {
                if (GitHubReleases.ParseRepo(project.GitHubUrl) is not { } repo)
                {
                    if (project.GitHubRelease != null)
                    {
                        project.GitHubRelease = null; // the link was removed
                        state.MarkDirty(project, touch: false);
                        changed = true;
                    }
                    continue;
                }
                var last = project.GitHubRelease;
                var fresh = last != null && last.Repository == repo.Url && last.Error == null && DateTime.Now - last.CheckedAt < MaxAge;
                if (!force && fresh)
                {
                    if (last!.Tag != null) released.Add(project.Name);
                    continue;
                }
                var info = await GitHubReleases.LatestReleaseAsync(Http, repo, token);
                project.GitHubRelease = info;
                state.MarkDirty(project, touch: false);
                changed = true;
                checkedCount++;
                if (info.Tag != null) released.Add(project.Name);
                if (info.Error != null) failed.Add($"{project.Name} ({info.Error})");
            }

            var summary = new List<string>();
            if (linked.Count > 0) summary.Add($"Linked {Count(linked.Count, "project")} to GitHub by name: {string.Join(", ", linked.Select(l => l.Project.Name))}.");
            summary.Add(released.Count == 0 ? "No published releases found." : $"Released on GitHub: {string.Join(", ", released)}.");
            if (failed.Count > 0) summary.Add($"Couldn't check: {string.Join("; ", failed)}.");
            if (!force && checkedCount == 0 && linked.Count == 0) summary.Add("(Checked recently; use Check releases now in Settings to ask again.)");
            return string.Join(" ", summary.Concat(notes));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "Could not check GitHub: " + ex.Message;
        }
        finally
        {
            _running = false;
            if (changed) Updated?.Invoke();
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>The token of the GitHub CLI's sign-in, or null when gh is missing or signed out. It is never stored.</summary>
    private static string? GitHubCliToken()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gh", "auth token")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch (Exception) { }
                return null;
            }
            var token = output.Result.Trim();
            return process.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (Exception)
        {
            return null; // gh is not installed
        }
    }
}
