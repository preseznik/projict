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
    /// <summary>People connected from another ProJict (key: their connection) and the project they have open.</summary>
    private readonly Dictionary<object, (string Name, Guid? Project)> _people = [];

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

    public void PersonConnected(object key, string name)
    {
        _people[key] = (name, null);
        Changed?.Invoke();
    }

    public void PersonViewing(object key, Guid? projectId)
    {
        if (!_people.TryGetValue(key, out var person) || person.Project == projectId) return;
        _people[key] = (person.Name, projectId);
        Changed?.Invoke();
    }

    public void PersonDisconnected(object key)
    {
        if (_people.Remove(key)) Changed?.Invoke();
    }

    /// <summary>The agents working in a project and the guests looking at it, one per name, in the order they connected.</summary>
    public IReadOnlyList<string> AgentsIn(Guid projectId) =>
        _projects.Where(kv => kv.Value.Contains(projectId)).Select(kv => kv.Key.AgentName)
            .Concat(_people.Values.Where(p => p.Project == projectId).Select(p => p.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
