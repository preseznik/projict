using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using ProJict.App.ViewModels;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// Keeps projects' GitHub issues in step (Details › GitHub issues): when ProJict starts, when asked, and a few seconds
/// after you finish or reopen an issue's item in a project that closes issues on GitHub.
/// </summary>
public sealed class IssueSyncService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly TimeSpan PushDelay = TimeSpan.FromSeconds(4);
    private readonly AppState _state;
    private readonly MainViewModel _main;
    private readonly HashSet<Guid> _running = [];
    /// <summary>Projects whose last push failed: no automatic retry (it would fail again) until the next sync you start.</summary>
    private readonly HashSet<Guid> _pushFailed = [];
    private readonly Dictionary<Guid, DispatcherTimer> _pushTimers = [];

    public IssueSyncService(AppState state, MainViewModel main)
    {
        _state = state;
        _main = main;
        state.ProjectChanged += SchedulePush;
    }

    /// <summary>Raised after a sync, so the project's Details tab shows the result.</summary>
    public event Action<Project>? Synced;

    public bool IsSyncing(Project project) => _running.Contains(project.Id);

    /// <summary>Syncs, one after another, every project that keeps its issues in sync.</summary>
    public async Task SyncAllAsync()
    {
        foreach (var project in _state.Projects.Where(p => p.Remote == null && p.Issues is { Enabled: true } && GitHubReleases.ParseRepo(p.GitHubUrl) != null).ToList())
            await SyncAsync(project, manual: false);
    }

    /// <summary>Imports new issues, applies what changed on GitHub and, when the project closes issues on GitHub, pushes what changed here.</summary>
    public async Task<string> SyncAsync(Project project, bool manual)
    {
        if (project.Remote != null) return "This project's GitHub issues are synced by the ProJict it comes from.";
        if (GitHubReleases.ParseRepo(project.GitHubUrl) is not { } repo) return "Enter the project's GitHub URL first.";
        if (!_running.Add(project.Id)) return "Already syncing with GitHub…";
        if (manual) _pushFailed.Remove(project.Id);
        Synced?.Invoke(project);
        string result;
        try
        {
            var token = _state.AppSettings.UseGitHubCliSignIn ? await Task.Run(GitHubService.GitHubCliToken) : null;
            var started = DateTime.Now;
            var fetched = await GitHubIssues.FetchAsync(Http, repo, project.Issues?.SyncedAt, token);
            if (!_state.Projects.Contains(project)) return "The project was deleted.";

            var vm = _main.GetProjectViewModel(project);
            IssueSyncOutcome outcome = null!;
            vm.ExternalChange(() =>
            {
                outcome = GitHubIssues.Apply(project, fetched);
                return outcome.Changed;
            });
            project.Issues!.SyncedAt = started;
            result = outcome.ToString();
            if (project.Issues.CloseOnGitHub) result += await PushAsync(project, repo, token, vm);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            result = "couldn't sync: " + ex.Message;
        }
        finally
        {
            _running.Remove(project.Id);
        }
        result = char.ToUpperInvariant(result[0]) + result[1..] + ".";
        if (project.Issues != null)
        {
            project.Issues.LastResult = $"{DateTime.Now:d MMM HH:mm}: {result}";
            _state.MarkDirty(project, touch: false);
        }
        if (manual || result.StartsWith("Couldn't", StringComparison.Ordinal)) _state.StatusText = $"{project.Name} › GitHub issues: {result}";
        Synced?.Invoke(project);
        return result;
    }

    /// <summary>Closes or reopens issues to match their items; the item's history says so. Returns a note for the result.</summary>
    private async Task<string> PushAsync(Project project, GitHubRepo repo, string? token, ProjectViewModel vm)
    {
        var pending = GitHubIssues.Pending(project);
        if (pending.Count == 0) return "";
        if (token == null)
        {
            _pushFailed.Add(project.Id);
            return "; closing issues on GitHub needs the GitHub CLI sign-in (Settings › GitHub)";
        }
        var done = new List<IssuePush>();
        string? error = null;
        foreach (var push in pending)
        {
            try { await GitHubIssues.SetStateAsync(Http, repo, push.Number, push.Close, token); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                error = ex.Message;
                _pushFailed.Add(project.Id);
                break;
            }
            done.Add(push);
        }
        if (done.Count > 0)
            vm.ExternalChange(() =>
            {
                foreach (var push in done)
                {
                    if (project.Find(push.ItemId)?.Item is not { Issue: { } link } item) continue;
                    link.Closed = push.Close;
                    item.AddActivity(_state.UserName, push.Close ? $"Closed issue #{push.Number} on GitHub." : $"Reopened issue #{push.Number} on GitHub.", agent: false);
                }
                return true;
            });
        var closed = done.Count(p => p.Close);
        var reopened = done.Count - closed;
        var note = (closed > 0 ? $"; closed {closed} on GitHub" : "") + (reopened > 0 ? $"; reopened {reopened} on GitHub" : "");
        return error == null ? note : $"{note}; {error}";
    }

    /// <summary>A change in a project that closes issues on GitHub: if an item now differs from its issue, sync in a moment.</summary>
    private void SchedulePush(Project project)
    {
        if (project.Remote != null || project.Issues is not { CloseOnGitHub: true } || _running.Contains(project.Id) || _pushFailed.Contains(project.Id)) return;
        if (GitHubIssues.Pending(project).Count == 0) return;
        if (!_pushTimers.TryGetValue(project.Id, out var timer))
        {
            timer = new DispatcherTimer { Interval = PushDelay };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                if (_state.Projects.Contains(project) && GitHubIssues.Pending(project).Count > 0) await SyncAsync(project, manual: false);
            };
            _pushTimers[project.Id] = timer;
        }
        timer.Stop();
        timer.Start();
    }
}
