using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>
/// The Agents view: the AI agents connected right now, each with what it's doing (live), what it has checked out and
/// what it did lately; below them, older agent work from the projects' history, greyed out. Optionally for one project.
/// </summary>
public sealed partial class AgentActivityViewModel : ObservableObject
{
    public const string AllProjects = "All projects";
    /// <summary>An agent counts as busy this long after its last call, or after setting its status.</summary>
    public static readonly TimeSpan BusyFor = TimeSpan.FromSeconds(30);
    private const int HistoryShown = 60;

    private readonly AppState _state;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _clock;

    public AgentActivityViewModel(AppState state, MainViewModel main)
    {
        _state = state;
        _main = main;
        // Ages ("12 s ago") and busy/idle move on by themselves; only while the view shows.
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Tick();
    }

    [ObservableProperty] private string _projectFilter = AllProjects;
    public List<string> ProjectChoices { get; private set; } = [AllProjects];
    public List<AgentCardViewModel> Live { get; private set; } = [];
    public List<AgentHistoryRow> History { get; private set; } = [];
    public bool HasLive => Live.Count > 0;
    public bool HasHistory => History.Count > 0;

    public string Subtitle => ProjectFilter == AllProjects ? "What AI agents are doing in your projects" : $"What AI agents are doing in {ProjectFilter}";

    public string EmptyText => _state.AppSettings.AllowAgents || _main.Api.IsRunning
        ? ProjectFilter == AllProjects
            ? "No AI agent is connected right now. Agents show up here as soon as they connect, with what they're doing."
            : $"No AI agent is working in {ProjectFilter} right now."
        : "AI agents can't connect: they're turned off in Settings › AI agents.";

    partial void OnProjectFilterChanged(string value) => Refresh();

    /// <summary>Shows the view, for one project or all of them.</summary>
    public void Show(Project? project)
    {
        ProjectChoices = [AllProjects, .. _state.Projects.Where(p => p.Remote == null).Select(p => p.Name).Order(StringComparer.CurrentCultureIgnoreCase)];
        OnPropertyChanged(nameof(ProjectChoices));
        ProjectFilter = project?.Name ?? AllProjects;
        Refresh();
        _clock.Start();
    }

    public void Hide() => _clock.Stop();

    private Project? FilterProject => ProjectFilter == AllProjects ? null : _state.Projects.FirstOrDefault(p => p.Name == ProjectFilter);

    /// <summary>Rebuilds the cards and the history (when agents connect, act or leave, and on open).</summary>
    public void Refresh()
    {
        var project = FilterProject;
        Live = _main.Agents.Sessions
            .Where(s => project == null || _main.Agents.ProjectsOf(s).Contains(project.Id))
            .Select(s => new AgentCardViewModel(s, this, project))
            .ToList();
        History = ActivityFeed.Build(_state.Projects.Where(p => p.Remote == null && (project == null || p == project)), _state.Settings, _state.UserName)
            .Where(e => e.Agent)
            .Take(HistoryShown)
            .Select(e => new AgentHistoryRow(e, _main))
            .ToList();
        foreach (var name in new[] { nameof(Live), nameof(History), nameof(HasLive), nameof(HasHistory), nameof(Subtitle), nameof(EmptyText) })
            OnPropertyChanged(name);
    }

    /// <summary>An agent did something: its card shows it at once.</summary>
    public void OnRecorded(McpSession session)
    {
        if (Live.FirstOrDefault(c => c.Session == session) is { } card) card.Refresh();
        else Refresh();
    }

    private void Tick()
    {
        foreach (var card in Live) card.Tick();
    }

    internal string ProjectName(Guid? id) => id is { } p ? _state.Projects.FirstOrDefault(x => x.Id == p)?.Name ?? "" : "";

    internal IReadOnlyList<string> ProjectNames(McpSession session) =>
        _main.Agents.ProjectsOf(session).Select(id => ProjectName(id)).Where(n => n.Length > 0).ToList();

    /// <summary>Items the agent has checked out right now.</summary>
    internal IReadOnlyList<(Project Project, TodoItem Item)> CheckedOut(McpSession session, Project? only) =>
        _state.Projects.Where(p => p.Remote == null && (only == null || p == only))
            .SelectMany(p => p.Walk().Select(l => (Project: p, l.Item)))
            .Where(x => x.Item.State == ItemState.InProgress && session.Claimed.Contains(x.Item.Id) &&
                        string.Equals(x.Item.ClaimedBy, session.AgentName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    internal void Reveal(Project project, Guid itemId)
    {
        var board = _main.OpenProject(project);
        if (project.Find(itemId) is { } loc) board.Reveal(loc.Item);
    }

    /// <summary>"now", "12 s ago", "3 min ago", "10:42".</summary>
    internal static string Age(DateTime at, DateTime now)
    {
        var age = now - at;
        if (age < TimeSpan.FromSeconds(5)) return "now";
        if (age < TimeSpan.FromMinutes(1)) return $"{(int)age.TotalSeconds} s ago";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        return at.Date == now.Date ? at.ToString("HH:mm", CultureInfo.CurrentCulture) : at.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>For a project's badges in the sidebar: who is there and what each did last.</summary>
    public string ToolTipFor(Project project)
    {
        var sessions = _main.Agents.Sessions.Where(s => _main.Agents.ProjectsOf(s).Contains(project.Id)).ToList();
        if (sessions.Count == 0) return "";
        var now = DateTime.Now;
        var lines = sessions.Select(s =>
        {
            var last = s.Recent.LastOrDefault(a => a.ProjectId == project.Id) ?? s.Recent.LastOrDefault();
            var doing = s.Status is { } status && s.StatusAt is { } since && now - since < TimeSpan.FromMinutes(15) ? status
                : last != null ? $"{last.Text} ({Age(last.At, now)})" : "connected, nothing done yet";
            return $"{s.AgentName}: {doing}";
        });
        return string.Join("\n", lines) + "\nRight-click › Agents shows more.";
    }
}

/// <summary>One connected agent: name, where it works, what it's doing now (with dots while busy), its check-outs and latest actions.</summary>
public sealed partial class AgentCardViewModel : ObservableObject
{
    private readonly AgentActivityViewModel _owner;
    private readonly Project? _only;

    public AgentCardViewModel(McpSession session, AgentActivityViewModel owner, Project? only)
    {
        Session = session;
        _owner = owner;
        _only = only;
        Refresh();
    }

    public McpSession Session { get; }
    public string Name => Session.AgentName;
    public WorkerBadge Badge => WorkerBadge.For([Session.AgentName])[0];
    public string Where { get; private set; } = "";
    public List<AgentActionRow> Recent { get; private set; } = [];
    public List<CheckoutRow> CheckedOut { get; private set; } = [];
    public bool HasCheckedOut => CheckedOut.Count > 0;
    public bool HasRecent => Recent.Count > 0;

    /// <summary>Called or set its status in the last half minute: the dots move.</summary>
    public bool IsBusy { get; private set; }
    public string StatusText { get; private set; } = "";
    public string StatusAge { get; private set; } = "";

    public void Refresh()
    {
        var names = _owner.ProjectNames(Session);
        Where = (names.Count == 0 ? "Connected" : "In " + string.Join(", ", names)) + $" · since {Session.ConnectedAt:HH:mm}";
        var actions = Session.Recent.Where(a => _only == null || a.ProjectId == null || a.ProjectId == _only.Id).ToList();
        Recent = actions.AsEnumerable().Reverse().Take(6).Select((a, i) => new AgentActionRow(a, i == 0, _owner)).ToList();
        CheckedOut = _owner.CheckedOut(Session, _only).Select(x => new CheckoutRow(x.Project, x.Item, _owner)).ToList();
        foreach (var name in new[] { nameof(Name), nameof(Badge), nameof(Where), nameof(Recent), nameof(CheckedOut), nameof(HasCheckedOut), nameof(HasRecent) })
            OnPropertyChanged(name);
        Tick();
    }

    public void Tick()
    {
        var now = DateTime.Now;
        var last = Session.Recent.LastOrDefault(a => _only == null || a.ProjectId == null || a.ProjectId == _only.Id);
        var statusFresh = Session.Status != null && Session.StatusAt is { } since && now - since < TimeSpan.FromMinutes(15);
        // Its own words while they're fresh, else what it last did.
        (StatusText, var at) = statusFresh ? (Session.Status!, Session.StatusAt!.Value)
            : last != null ? (last.Text, last.At)
            : ("Connected, nothing done yet", Session.ConnectedAt);
        IsBusy = now - (Session.LastActive ?? Session.ConnectedAt) < AgentActivityViewModel.BusyFor ||
                 (statusFresh && now - Session.StatusAt!.Value < AgentActivityViewModel.BusyFor);
        StatusAge = IsBusy ? Age(at, now) : $"idle · {Age(at, now)}";
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusAge));
        OnPropertyChanged(nameof(IsBusy));
        foreach (var row in Recent) row.Tick(now);
    }

    private static string Age(DateTime at, DateTime now) => AgentActivityViewModel.Age(at, now);
}

/// <summary>One of an agent's latest actions; the newest stands out, the rest are quieter.</summary>
public sealed partial class AgentActionRow(AgentAction action, bool isLatest, AgentActivityViewModel owner) : ObservableObject
{
    public string Text => action.Text;
    public bool IsLatest => isLatest;
    public string Project => owner.ProjectName(action.ProjectId);
    public string Age { get; private set; } = AgentActivityViewModel.Age(action.At, DateTime.Now);
    public string Time => action.At.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public void Tick(DateTime now)
    {
        Age = AgentActivityViewModel.Age(action.At, now);
        OnPropertyChanged(nameof(Age));
    }
}

/// <summary>An item the agent has checked out; a click shows it on its board.</summary>
public sealed partial class CheckoutRow(Project project, TodoItem item, AgentActivityViewModel owner)
{
    public string Text => item.DisplayText().Replace("\r\n", " ").Replace('\n', ' ');
    public string Project => project.Name;

    [RelayCommand] private void Open() => owner.Reveal(project, item.Id);
}

/// <summary>Older agent work from the projects' history; a click goes where it happened, as in the Feed.</summary>
public sealed partial class AgentHistoryRow(FeedEvent e, MainViewModel main)
{
    public string When => e.At.Date == DateTime.Today ? e.At.ToString("HH:mm", CultureInfo.CurrentCulture) : e.At.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    public string Who => e.Who;
    public string Action => e.Action;
    public string Subject => e.Subject.Replace("\r\n", " ").Replace('\n', ' ');
    public string? Detail => e.Detail?.Replace("\r\n", " ").Replace('\n', ' ');
    public string Where => e.Category != null ? $"{e.Project?.Name} › {e.Category.Name}" : e.Project?.Name ?? e.Group ?? "";

    [RelayCommand] private void Open() => main.Feed.Open(e);
}
