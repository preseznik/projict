namespace ProJict.Core.Model;

public static class Priorities
{
    /// <summary>"urgent", "high", "medium" (or "med"), "low" or "none"/"" (case-insensitive); null when it's none of those.</summary>
    public static Priority? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "urgent" => Priority.Urgent,
        "high" => Priority.High,
        "medium" or "med" => Priority.Medium,
        "low" => Priority.Low,
        "none" or "" => Priority.None,
        _ => null,
    };

    /// <summary>Most pressing first; items with the same priority keep their own order (OrderByDescending is stable).</summary>
    public static IEnumerable<TodoItem> ByPriority(this IEnumerable<TodoItem> items) => items.OrderByDescending(i => i.Priority);
}
