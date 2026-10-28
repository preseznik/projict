using ProJict.Core.Model;

namespace ProJict.Core.Storage;

/// <summary>Adding, renaming and deleting sidebar groups. Personal and Work can't be renamed or deleted.</summary>
public static class GroupOperations
{
    /// <summary>Why <paramref name="name"/> can't be used as a group name, or null when it can.</summary>
    public static string? Validate(WorkspaceSettings settings, string? name, string? renaming = null)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) return "Enter a name for the group.";
        if (Same(name, WorkspaceSettings.ArchivedGroup)) return "\"Archived\" is where archived projects are listed; pick another name.";
        if (settings.Groups.Any(g => Same(g, name) && !Same(g, renaming))) return $"There is already a group called \"{name}\".";
        return null;
    }

    public static void Add(WorkspaceSettings settings, string name)
    {
        if (Validate(settings, name) is { } error) throw new ArgumentException(error, nameof(name));
        settings.Groups.Add(name.Trim());
    }

    /// <summary>Renames a group and moves its projects along; returns the projects that changed.</summary>
    public static List<Project> Rename(Workspace workspace, string oldName, string newName)
    {
        if (WorkspaceSettings.IsDefaultGroup(oldName)) throw new InvalidOperationException($"{oldName} can't be renamed.");
        if (Validate(workspace.Settings, newName, renaming: oldName) is { } error) throw new ArgumentException(error, nameof(newName));
        newName = newName.Trim();
        var settings = workspace.Settings;
        var i = settings.Groups.FindIndex(g => Same(g, oldName));
        if (i >= 0) settings.Groups[i] = newName;
        else settings.Groups.Add(newName);
        settings.CollapsedGroups = settings.CollapsedGroups.Select(g => Same(g, oldName) ? newName : g).ToList();
        return MoveProjects(workspace, oldName, newName);
    }

    /// <summary>Deletes a group; its projects move to Personal. Returns the projects that moved.</summary>
    public static List<Project> Delete(Workspace workspace, string name)
    {
        if (WorkspaceSettings.IsDefaultGroup(name)) throw new InvalidOperationException($"{name} can't be deleted.");
        var settings = workspace.Settings;
        settings.Groups.RemoveAll(g => Same(g, name));
        settings.CollapsedGroups = settings.CollapsedGroups.Where(g => !Same(g, name)).ToList();
        return MoveProjects(workspace, name, WorkspaceSettings.PersonalGroup);
    }

    private static List<Project> MoveProjects(Workspace workspace, string from, string to)
    {
        var moved = workspace.Projects.Where(p => !p.IsInbox && Same(p.Group, from)).ToList();
        foreach (var project in moved) project.Group = to;
        return moved;
    }

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
