namespace ProJict.Core.Model;

public static class Priorities
{
    /// <summary>"urgent", "high", "medium" (or "med"), "low", "hold" (or "on hold") or "none"/"" (case-insensitive); null when it's none of those.</summary>
    public static Priority? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "urgent" => Priority.Urgent,
        "high" => Priority.High,
        "medium" or "med" => Priority.Medium,
        "low" => Priority.Low,
        "hold" or "on hold" => Priority.Hold,
        "none" or "" => Priority.None,
        _ => null,
    };

    /// <summary>Most pressing is highest: urgent, high, medium, low, then items without a priority, and items on hold last.</summary>
    public static int Rank(Priority priority) => priority switch
    {
        Priority.Urgent => 5,
        Priority.High => 4,
        Priority.Medium => 3,
        Priority.Low => 2,
        Priority.None => 1,
        _ => 0,
    };

    /// <summary>The item's priority is set and at least <paramref name="min"/> (so never None or Hold).</summary>
    public static bool AtLeast(Priority priority, Priority min) => priority is not (Priority.None or Priority.Hold) && Rank(priority) >= Rank(min);

    /// <summary>"Urgent", "High", …, "On hold"; empty for None.</summary>
    public static string Label(Priority priority) => priority switch
    {
        Priority.None => "",
        Priority.Hold => "On hold",
        _ => priority.ToString(),
    };

    /// <summary>Most pressing first; items with the same priority keep their own order (OrderByDescending is stable).</summary>
    public static IEnumerable<TodoItem> ByPriority(this IEnumerable<TodoItem> items) => items.OrderByDescending(i => Rank(i.Priority));
}
