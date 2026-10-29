using ProJict.Core.Agents;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// The AI agents connected to ProJict and the projects each is working in: the one matching its working folder and
/// any it reads or changes. Shown as badges next to projects. Used on the UI thread only.
/// </summary>
public sealed class AgentPresence
{
    private readonly Dictionary<McpSession, HashSet<Guid>> _projects = [];

    /// <summary>Raised when an agent connects, disconnects, renames itself or starts working in another project.</summary>
    public event Action? Changed;

    public void Connected(McpSession session, Project? folderProject)
    {
        _projects[session] = folderProject == null ? [] : [folderProject.Id];
        Changed?.Invoke();
    }

    public void Disconnected(McpSession session)
    {
        if (_projects.Remove(session)) Changed?.Invoke();
    }

    public void WorkingIn(McpSession session, Project project)
    {
        if (_projects.TryGetValue(session, out var ids) && ids.Add(project.Id)) Changed?.Invoke();
    }

    /// <summary>An agent's name changed (set_label).</summary>
    public void Renamed() => Changed?.Invoke();

    /// <summary>The agents working in a project, one per name, in the order they connected.</summary>
    public IReadOnlyList<string> AgentsIn(Guid projectId) =>
        _projects.Where(kv => kv.Value.Contains(projectId)).Select(kv => kv.Key.AgentName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
