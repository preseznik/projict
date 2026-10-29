using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>An issue or a pull request (GitHub lists both as issues).</summary>
public sealed record GitHubIssue(
    int Number, string Title, string Url, bool Closed, IReadOnlyList<string> Labels, DateTime CreatedAt,
    string? Author = null, string? StateReason = null, bool IsPullRequest = false, bool Merged = false, string? Body = null);

/// <summary>An item whose state here differs from its issue on GitHub: close or reopen the issue.</summary>
public sealed record IssuePush(Guid ItemId, int Number, bool Close);

/// <summary>What one sync changed in the project.</summary>
public sealed class IssueSyncOutcome
{
    public int Added { get; set; }
    public int Closed { get; set; }
    public int Reopened { get; set; }
    public int Renamed { get; set; }
    public int PullRequests { get; set; }

    public bool Changed => Added + Closed + Reopened + Renamed + PullRequests > 0;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Added > 0) parts.Add(Added == 1 ? "1 new issue" : $"{Added} new issues");
        if (Closed > 0) parts.Add($"{Closed} closed on GitHub");
        if (Reopened > 0) parts.Add($"{Reopened} reopened on GitHub");
        if (Renamed > 0) parts.Add($"{Renamed} renamed");
        if (PullRequests > 0) parts.Add(PullRequests == 1 ? "1 pull request linked or updated" : $"{PullRequests} pull requests linked or updated");
        return parts.Count == 0 ? "nothing new" : string.Join(", ", parts);
    }
}

/// <summary>
/// Brings a repository's issues into a project as items of a GITHUB category and keeps them in step: an issue closed or
/// reopened on GitHub closes or reopens its item, and pull requests that mention an issue ("Fixes #12") show on its item.
/// </summary>
public static partial class GitHubIssues
{
    public const string CategoryName = "GITHUB";
    /// <summary>Signs history entries for what happened on GitHub.</summary>
    public const string Signature = "GitHub";
    private const string Api = "https://api.github.com";
    private const int MaxPages = 10;

    [GeneratedRegex(@"(?<![\w/&])#(\d{1,7})\b")]
    private static partial Regex IssueReference();

    /// <summary>
    /// The first sync asks for open issues (and pull requests); later ones for everything changed since the last one,
    /// with a few minutes' overlap, so closed and reopened issues come along.
    /// </summary>
    public static string ListUrl(GitHubRepo repo, DateTime? since, int page)
    {
        var url = $"{Api}/repos/{repo.Owner}/{repo.Name}/issues?per_page=100&page={page}";
        return since is { } s
            ? url + "&state=all&since=" + s.ToUniversalTime().AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : url + "&state=open";
    }

    public static async Task<List<GitHubIssue>> FetchAsync(HttpClient http, GitHubRepo repo, DateTime? since, string? token, CancellationToken ct = default)
    {
        var all = new List<GitHubIssue>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var json = await GitHubReleases.GetAsync(http, ListUrl(repo, since, page), token, ct).ConfigureAwait(false)
                       ?? throw new HttpRequestException(token == null
                           ? $"{repo.Owner}/{repo.Name} wasn't found on GitHub (private repositories need the GitHub CLI sign-in in Settings › GitHub)."
                           : $"{repo.Owner}/{repo.Name} wasn't found on GitHub.");
            var batch = Parse(json);
            all.AddRange(batch);
            if (batch.Count < 100) break;
        }
        return all;
    }

    /// <summary>Closes (as completed) or reopens an issue; needs a sign-in that may change the repository.</summary>
    public static async Task SetStateAsync(HttpClient http, GitHubRepo repo, int number, bool close, string token, CancellationToken ct = default)
    {
        var body = close ? """{"state":"closed","state_reason":"completed"}""" : """{"state":"open"}""";
        var request = new HttpRequestMessage(HttpMethod.Patch, $"{Api}/repos/{repo.Owner}/{repo.Name}/issues/{number}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        try
        {
            if (await GitHubReleases.SendAsync(http, request, token, ct).ConfigureAwait(false) == null)
                throw new HttpRequestException($"Issue #{number} wasn't found on GitHub.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.UnprocessableEntity)
        {
            throw new HttpRequestException($"GitHub didn't let ProJict {(close ? "close" : "reopen")} issue #{number} (can your GitHub CLI sign-in change {repo.Owner}/{repo.Name}?).", ex, ex.StatusCode);
        }
    }

    /// <summary>Issues and pull requests from GitHub's issue list JSON.</summary>
    public static List<GitHubIssue> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<GitHubIssue>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var isPull = e.TryGetProperty("pull_request", out var pull) && pull.ValueKind == JsonValueKind.Object;
            list.Add(new GitHubIssue(
                Number: e.GetProperty("number").GetInt32(),
                Title: (e.GetProperty("title").GetString() ?? "").Trim(),
                Url: String(e, "html_url") ?? "",
                Closed: String(e, "state") == "closed",
                Labels: e.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                    ? labels.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : String(l, "name")).OfType<string>().ToList()
                    : [],
                CreatedAt: e.TryGetProperty("created_at", out var at) && at.ValueKind == JsonValueKind.String ? at.GetDateTime().ToLocalTime() : DateTime.Now,
                Author: e.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object ? String(user, "login") : null,
                StateReason: String(e, "state_reason"),
                IsPullRequest: isPull,
                Merged: isPull && String(pull, "merged_at") != null,
                Body: String(e, "body")));
        }
        return list;

        static string? String(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>bug → BUG; enhancement or feature → ADD; anything else no tag.</summary>
    public static string? TagFor(IEnumerable<string> labels)
    {
        var names = labels.Select(l => l.ToLowerInvariant()).ToList();
        if (names.Any(l => l.Contains("bug"))) return "BUG";
        if (names.Any(l => l.Contains("enhancement") || l.Contains("feature"))) return "ADD";
        return null;
    }

    /// <summary>Issue numbers a pull request's title or description mentions ("Fixes #12", "see #3").</summary>
    public static HashSet<int> References(GitHubIssue pull) =>
        IssueReference().Matches($"{pull.Title}\n{pull.Body}")
            .Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0).Where(n => n > 0 && n != pull.Number).ToHashSet();

    /// <summary>
    /// Applies what GitHub returned to the project: new open issues become items (once; an item you delete stays gone),
    /// a change of state or title on GitHub carries over to its item, and pull requests are linked to the issues they mention.
    /// </summary>
    public static IssueSyncOutcome Apply(Project project, IReadOnlyList<GitHubIssue> fetched)
    {
        var sync = project.Issues ??= new IssueSync();
        var outcome = new IssueSyncOutcome();
        var linked = project.Walk().Where(l => l.Item.Issue != null)
            .GroupBy(l => l.Item.Issue!.Number).ToDictionary(g => g.Key, g => g.First().Item);
        var imported = sync.Imported.ToHashSet();

        foreach (var issue in fetched.Where(i => !i.IsPullRequest).OrderBy(i => i.Number))
        {
            if (linked.TryGetValue(issue.Number, out var item))
            {
                var link = item.Issue!;
                link.Url = issue.Url;
                link.Author ??= issue.Author;
                if (issue.Title.Length > 0 && link.Title != issue.Title)
                {
                    if (item.Text == link.Title)
                    {
                        item.Text = issue.Title;
                        outcome.Renamed++;
                    }
                    link.Title = issue.Title;
                }
                if (issue.Closed == link.Closed) continue;
                link.Closed = issue.Closed;
                if (issue.Closed && !item.IsDone)
                {
                    item.SetState(ItemState.Done);
                    item.AddActivity(Signature, issue.StateReason == "not_planned" ? "Closed the issue as not planned." : "Closed the issue.", agent: false);
                    outcome.Closed++;
                }
                else if (!issue.Closed && item.IsDone)
                {
                    item.SetState(ItemState.Todo);
                    item.AddActivity(Signature, "Reopened the issue.", agent: false);
                    outcome.Reopened++;
                }
            }
            else if (!issue.Closed && imported.Add(issue.Number))
            {
                var added = new TodoItem
                {
                    Text = issue.Title.Length > 0 ? issue.Title : $"Issue #{issue.Number}",
                    Tag = TagFor(issue.Labels),
                    CreatedAt = issue.CreatedAt,
                    Issue = new IssueLink { Number = issue.Number, Url = issue.Url, Title = issue.Title, Author = issue.Author },
                };
                var category = project.FindCategory(CategoryName);
                if (category == null) project.Categories.Add(category = new Category { Name = CategoryName });
                category.Items.Add(added);
                sync.Imported.Add(issue.Number);
                linked[issue.Number] = added;
                outcome.Added++;
            }
        }

        foreach (var pull in fetched.Where(i => i.IsPullRequest))
        {
            var mentions = References(pull);
            var state = pull.Merged ? PullRequestState.Merged : pull.Closed ? PullRequestState.Closed : PullRequestState.Open;
            foreach (var (number, item) in linked)
            {
                var links = item.Issue!.PullRequests;
                var existing = links?.FirstOrDefault(p => p.Number == pull.Number);
                if (!mentions.Contains(number))
                {
                    if (existing == null) continue;
                    links!.Remove(existing); // the pull request no longer mentions it
                    if (links.Count == 0) item.Issue.PullRequests = null;
                    outcome.PullRequests++;
                    continue;
                }
                if (existing == null)
                {
                    (item.Issue.PullRequests ??= []).Add(new PullRequestLink { Number = pull.Number, Url = pull.Url, Title = pull.Title, State = state });
                    outcome.PullRequests++;
                }
                else if (existing.State != state || existing.Title != pull.Title)
                {
                    existing.State = state;
                    existing.Title = pull.Title;
                    existing.Url = pull.Url;
                    outcome.PullRequests++;
                }
            }
        }
        return outcome;
    }

    /// <summary>Items finished here whose issue is still open on GitHub, and reopened ones whose issue is closed.</summary>
    public static List<IssuePush> Pending(Project project) =>
        project.Walk().Where(l => l.Item.Issue is { } link && l.Item.IsDone != link.Closed)
            .Select(l => new IssuePush(l.Item.Id, l.Item.Issue!.Number, l.Item.IsDone)).ToList();
}
