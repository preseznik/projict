using ProJict.Core.Model;

namespace ProJict.Core.Agents;

/// <summary>
/// Which items AI agents may work on, by priority (Settings › AI agents). Agents can read and comment on every item,
/// but only check out, change, attach to or complete the ones whose priority the user allows. Items on hold are
/// always left alone. A sub-item without a priority of its own goes by the item it belongs to, and anything under
/// an item on hold is on hold too.
/// </summary>
public static class AgentAccess
{
    /// <summary>The choices in Settings, most pressing first (None = items without a priority).</summary>
    public static IReadOnlyList<Priority> Choices { get; } = [Priority.Urgent, Priority.High, Priority.Medium, Priority.Low, Priority.None];

    /// <summary>Every item with a priority set; not items without one, and never items on hold.</summary>
    public static IReadOnlyList<Priority> DefaultPriorities { get; } = [Priority.Urgent, Priority.High, Priority.Medium, Priority.Low];

    /// <summary>The priority that decides for an item, given the one deciding for the item above it (None for top-level items).</summary>
    public static Priority Governing(Priority own, Priority above) =>
        above == Priority.Hold ? Priority.Hold : own != Priority.None ? own : above;

    /// <summary>The deciding priority of every item in a project.</summary>
    public static Dictionary<TodoItem, Priority> Governing(Project project)
    {
        var result = new Dictionary<TodoItem, Priority>(ReferenceEqualityComparer.Instance);
        foreach (var category in project.Categories) Add(category.Items, Priority.None);
        return result;

        void Add(List<TodoItem> items, Priority above)
        {
            foreach (var item in items)
            {
                var priority = Governing(item.Priority, above);
                result[item] = priority;
                Add(item.Children, priority);
            }
        }
    }

    public static bool Allows(IEnumerable<Priority> allowed, Priority governing) =>
        governing != Priority.Hold && allowed.Contains(governing);

    /// <summary>Why agents may not work on an item with this deciding priority, for the agent; null when they may.</summary>
    public static string? Refusal(IReadOnlyCollection<Priority> allowed, Priority governing)
    {
        if (Allows(allowed, governing)) return null;
        if (governing == Priority.Hold) return "This item is on hold (or belongs to an item on hold): the user doesn't want AI agents working on it.";
        var what = governing == Priority.None ? "has no priority" : $"is {governing.ToString().ToLowerInvariant()} priority";
        var rule = Describe(allowed) is { } may ? $"lets AI agents work only on {may}" : "doesn't let AI agents work on any items at the moment";
        return $"This item {what}, and the user {rule} (their choice in ProJict's Settings › AI agents).";
    }

    /// <summary>"urgent, high, medium or low priority items", "items without a priority" and so on; null when no items.</summary>
    public static string? Describe(IReadOnlyCollection<Priority> allowed)
    {
        var levels = DefaultPriorities.Where(allowed.Contains).Select(p => p.ToString().ToLowerInvariant()).ToList();
        var parts = new List<string>();
        if (levels.Count > 0) parts.Add((levels.Count == 1 ? levels[0] : string.Join(", ", levels.SkipLast(1)) + " or " + levels[^1]) + " priority items");
        if (allowed.Contains(Priority.None)) parts.Add("items without a priority");
        return parts.Count == 0 ? null : string.Join(" and ", parts);
    }
}
