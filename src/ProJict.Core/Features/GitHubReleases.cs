using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProJict.Core.Model;

namespace ProJict.Core.Features;

public sealed record GitHubRepo(string Owner, string Name)
{
    public string Url => $"https://github.com/{Owner}/{Name}";
}

/// <summary>Finds projects' GitHub repositories and the newest release published in each.</summary>
public static partial class GitHubReleases
{
    private const string Api = "https://api.github.com";

    [GeneratedRegex(@"github\.com[/:](?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9._-]+?)(?:\.git)?/?(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex RepoPattern();

    /// <summary>Owner and name from a GitHub link (https, ssh or a page inside the repository); null otherwise.</summary>
    public static GitHubRepo? ParseRepo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = RepoPattern().Match(url.Trim());
        return m.Success ? new GitHubRepo(m.Groups["owner"].Value, m.Groups["repo"].Value) : null;
    }

    /// <summary>Lowercase letters and digits only, so "SplitSecond-HFR" matches "SplitSecondHFR".</summary>
    public static string MatchKey(string? name) =>
        new((name ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Projects without a GitHub link whose name or key matches exactly one repository.</summary>
    public static List<(Project Project, GitHubRepo Repo)> MatchUnlinked(IEnumerable<Project> projects, IEnumerable<GitHubRepo> repos)
    {
        var byKey = repos.GroupBy(r => MatchKey(r.Name)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
        var matches = new List<(Project, GitHubRepo)>();
        foreach (var project in projects.Where(p => !p.IsInbox && string.IsNullOrWhiteSpace(p.GitHubUrl)))
        {
            if (byKey.TryGetValue(MatchKey(project.Name), out var repo) || byKey.TryGetValue(MatchKey(project.Key), out repo))
                matches.Add((project, repo));
        }
        return matches;
    }

    /// <summary>Repositories from GitHub's repository list JSON.</summary>
    public static List<GitHubRepo> ParseRepos(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => new GitHubRepo(e.GetProperty("owner").GetProperty("login").GetString()!, e.GetProperty("name").GetString()!))
            .ToList();
    }

    /// <summary>
    /// The newest published release from GitHub's release list JSON (newest first): a stable one when there is any,
    /// else the newest pre-release. Drafts are skipped; Tag stays null when nothing is published.
    /// </summary>
    public static GitHubReleaseInfo ParseReleases(string json, GitHubRepo repo, DateTime checkedAt)
    {
        var info = new GitHubReleaseInfo { Repository = repo.Url, CheckedAt = checkedAt };
        using var doc = JsonDocument.Parse(json);
        var published = doc.RootElement.EnumerateArray().Where(e => !IsTrue(e, "draft")).ToList();
        var newest = published.FirstOrDefault(e => !IsTrue(e, "prerelease"));
        if (newest.ValueKind == JsonValueKind.Undefined)
        {
            newest = published.FirstOrDefault();
            if (newest.ValueKind == JsonValueKind.Undefined) return info;
            info.Prerelease = true;
        }
        info.Tag = newest.GetProperty("tag_name").GetString();
        info.Url = newest.TryGetProperty("html_url", out var url) ? url.GetString() : null;
        if (newest.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String)
            info.PublishedAt = at.GetDateTime().ToLocalTime();
        return info;

        static bool IsTrue(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// With a token: the signed-in user's repositories (private ones too), narrowed to <paramref name="account"/> when given.
    /// Without: the public repositories of <paramref name="account"/>.
    /// </summary>
    public static async Task<List<GitHubRepo>> ListReposAsync(HttpClient http, string? account, string? token, CancellationToken ct = default)
    {
        account = string.IsNullOrWhiteSpace(account) ? null : account.Trim();
        if (account == null && token == null) return [];
        var repos = new List<GitHubRepo>();
        for (var page = 1; page <= 10; page++)
        {
            var url = token != null
                ? $"{Api}/user/repos?per_page=100&page={page}&affiliation=owner,organization_member"
                : $"{Api}/users/{Uri.EscapeDataString(account!)}/repos?per_page=100&page={page}&type=owner";
            var json = await GetAsync(http, url, token, ct).ConfigureAwait(false);
            if (json == null) break;
            var batch = ParseRepos(json);
            repos.AddRange(batch);
            if (batch.Count < 100) break;
        }
        return account == null ? repos : repos.Where(r => string.Equals(r.Owner, account, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static async Task<GitHubReleaseInfo> LatestReleaseAsync(HttpClient http, GitHubRepo repo, string? token, CancellationToken ct = default)
    {
        var json = await GetAsync(http, $"{Api}/repos/{repo.Owner}/{repo.Name}/releases?per_page=10", token, ct).ConfigureAwait(false);
        if (json != null) return ParseReleases(json, repo, DateTime.Now);
        return new GitHubReleaseInfo
        {
            Repository = repo.Url,
            CheckedAt = DateTime.Now,
            Error = token == null ? "not found (private repositories need the GitHub CLI sign-in)" : "not found",
        };
    }

    /// <summary>The response body, or null for 404; throws HttpRequestException with a readable message otherwise.</summary>
    internal static async Task<string?> GetAsync(HttpClient http, string url, string? token, CancellationToken ct) =>
        await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, url), token, ct).ConfigureAwait(false);

    internal static async Task<string?> SendAsync(HttpClient http, HttpRequestMessage request, string? token, CancellationToken ct)
    {
        using var _ = request;
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ProJict", "1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests &&
            response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0")
            throw new HttpRequestException("GitHub's hourly request limit was reached; try again later (the GitHub CLI sign-in raises the limit).");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new HttpRequestException("GitHub didn't accept the GitHub CLI sign-in; run \"gh auth login\" again.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
