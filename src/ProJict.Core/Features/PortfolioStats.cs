using ProJict.Core.Model;

namespace ProJict.Core.Features;

public sealed record ProjectStats(int Open, int InProgress, int Done, int OpenBugs, DateTime LastActivity)
{
    public int Total => Open + Done;
    public double Progress => Total == 0 ? 0 : (double)Done / Total;
    public bool IsStale(int days, DateTime now) => days > 0 && now - LastActivity > TimeSpan.FromDays(days);
}

public sealed record RecentDone(Project Project, Category Category, TodoItem Item);

public static class PortfolioStats
{
    /// <summary>Counts top-level items, which is what a line in the TODO/DONE files represents.</summary>
    public static ProjectStats For(Project project)
    {
        int open = 0, inProgress = 0, done = 0, bugs = 0;
        foreach (var item in project.Categories.SelectMany(c => c.Items))
        {
            if (item.IsDone) { done++; continue; }
            open++;
            if (item.State == ItemState.InProgress) inProgress++;
            if (item.IsBugLike()) bugs++;
        }
        return new ProjectStats(open, inProgress, done, bugs, project.UpdatedAt);
    }

    /// <summary>Items finished in the app (not imported) since <paramref name="since"/>, newest first.</summary>
    public static List<RecentDone> RecentlyDone(IEnumerable<Project> projects, DateTime since) =>
        projects
            .SelectMany(p => p.Walk().Select(l => new RecentDone(p, l.Category, l.Item)))
            .Where(r => r.Item.IsDone && !r.Item.Imported && r.Item.DoneAt >= since)
            .OrderByDescending(r => r.Item.DoneAt)
            .ToList();
}
