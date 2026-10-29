using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Views;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.App.ViewModels;

public sealed partial class ProjectListItemViewModel(Project project, MainViewModel main) : ObservableObject
{
    public Project Project { get; } = project;
    public string Name => Project.Name;
    public ProjectStatus Status => Project.Status;
    public string DisplayGroup => ProjectGroups.Of(Project);
    public int GroupOrder => ProjectGroups.Order(Project, main.State.Settings);
    public int OpenCount => Project.Categories.Sum(c => c.Items.Count(i => !i.IsDone));

    /// <summary>Projects offered under "Copy categories from" in the right-click menu.</summary>
    public List<TemplateSourceViewModel> TemplateSources => main.TemplateSourcesFor(Project);

    /// <summary>Groups offered under "Move to group" in the right-click menu.</summary>
    public List<GroupChoiceViewModel> GroupChoices => main.GroupChoicesFor(Project);

    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Called when the right-click menu opens, so its submenus are current.</summary>
    public void RefreshMenu()
    {
        OnPropertyChanged(nameof(TemplateSources));
        OnPropertyChanged(nameof(GroupChoices));
    }

    /// <summary>Used as the accessible name of the sidebar entry.</summary>
    public override string ToString() => Name;
}

/// <summary>A project whose categories can be copied into another one.</summary>
public sealed class TemplateSourceViewModel(Project source, Action<Project> copy)
{
    public string Name => source.Name;
    public string Detail => source.Categories.Count == 1 ? "1 category" : $"{source.Categories.Count} categories";
    public void Copy() => copy(source);
}

/// <summary>An entry under "Move to group": an existing group, or "New group…" when the name is null.</summary>
public sealed class GroupChoiceViewModel(string? name, bool isCurrent, Action choose)
{
    public string Label => name ?? "New group…";
    public string Glyph => isCurrent ? "\uE73E" : name == null ? "\uE710" : "";
    public void Choose() => choose();
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<Guid, ProjectViewModel> _projectViewModels = [];
    private readonly PropertyGroupDescription _groupDescription =
        new(nameof(ProjectListItemViewModel.DisplayGroup), null, StringComparison.OrdinalIgnoreCase);
    private string _groupsSignature = "";
    private bool _syncingSelection;

    public MainViewModel(AppState state)
    {
        State = state;
        Updates = new Services.UpdateService(state);
        GitHub = new Services.GitHubService(state);
        Dashboard = new DashboardViewModel(state, this);
        GitHub.Updated += () =>
        {
            foreach (var vm in _projectViewModels.Values) vm.RefreshGitHubLink();
            if (CurrentView is DashboardViewModel) Dashboard.Refresh();
        };
        ProjectsView = CollectionViewSource.GetDefaultView(ProjectItems);
        ProjectsView.GroupDescriptions.Add(_groupDescription);
        ProjectsView.SortDescriptions.Add(new SortDescription(nameof(ProjectListItemViewModel.GroupOrder), ListSortDirection.Ascending));
        ProjectsView.SortDescriptions.Add(new SortDescription(nameof(ProjectListItemViewModel.Name), ListSortDirection.Ascending));
        SidebarFilter = new ProjectFilterViewModel(state, state.Settings.HiddenProjects);
        SidebarFilter.HiddenChanged += Dashboard.Filter.OnHiddenChangedElsewhere;
        Dashboard.Filter.HiddenChanged += SidebarFilter.OnHiddenChangedElsewhere;
        ProjectsView.Filter = o => o is ProjectListItemViewModel i && SidebarFilter.Matches(i.Project);
        SidebarFilter.Changed += () =>
        {
            SyncGroupNames();
            ProjectsView.Refresh();
            SyncSelection();
            OnPropertyChanged(nameof(NothingToShow));
        };
        SidebarFilter.Accepted += () =>
        {
            if (ProjectsView.OfType<ProjectListItemViewModel>().FirstOrDefault() is { } first) OpenProject(first.Project);
        };
        RebuildList();

        state.ProjectListChanged += RebuildList;
        state.ProjectChanged += p =>
        {
            if (p.IsInbox) OnPropertyChanged(nameof(InboxCount));
            ProjectItems.FirstOrDefault(i => i.Project == p)?.Refresh();
            // While the dashboard shows, changes come from elsewhere (an AI agent, quick capture); keep it current.
            if (CurrentView is DashboardViewModel) Dashboard.Refresh();
            if (Discussion is { } open && open.Target.ProjectId == p.Id) open.Sync();
        };
        state.GroupCommentsChanged += group =>
        {
            OnPropertyChanged(nameof(GroupCommentCounts));
            if (CurrentView is DashboardViewModel) Dashboard.Refresh();
            if (Discussion is { Target.Kind: DiscussionKind.Group } open && string.Equals(open.Target.Group, group, StringComparison.OrdinalIgnoreCase))
                open.Sync();
        };

        var last = state.AppSettings.LastProjectId is { } id ? state.Projects.FirstOrDefault(p => p.Id == id) : null;
        if (last != null) OpenProject(last);
        else ShowDashboard();
    }

    public AppState State { get; }
    public Services.UpdateService Updates { get; }
    public Services.GitHubService GitHub { get; }
    public DashboardViewModel Dashboard { get; }
    public ObservableCollection<ProjectListItemViewModel> ProjectItems { get; } = [];
    public ICollectionView ProjectsView { get; }
    public ProjectFilterViewModel SidebarFilter { get; }
    /// <summary>A search found nothing (otherwise the group headings still show).</summary>
    public bool NothingToShow => SidebarFilter.IsSearching && ProjectItems.Count > 0 && ProjectsView.IsEmpty;

    /// <summary>Replaced (not mutated) on every toggle, so the group headers' bindings update.</summary>
    public IReadOnlyList<string> CollapsedGroups => State.Settings.CollapsedGroups;
    public int InboxCount => State.Inbox.Categories.Sum(c => c.Items.Count(i => !i.IsDone));

    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private ProjectListItemViewModel? _selectedProjectItem;
    [ObservableProperty] private bool _isDashboardActive;
    [ObservableProperty] private bool _isInboxActive;

    public ProjectViewModel? CurrentProject => CurrentView as ProjectViewModel;

    partial void OnSelectedProjectItemChanged(ProjectListItemViewModel? value)
    {
        if (!_syncingSelection && value != null) OpenProject(value.Project);
    }

    partial void OnCurrentViewChanged(object? value) => OnPropertyChanged(nameof(CurrentProject));

    private void RebuildList()
    {
        var selected = SelectedProjectItem?.Project;
        _syncingSelection = true;
        ProjectItems.Clear();
        var projects = State.Projects.Where(p => !p.IsInbox).ToList();
        SidebarFilter.Rebuild(projects);
        foreach (var project in projects)
            ProjectItems.Add(new ProjectListItemViewModel(project, this));
        SyncGroups();
        SelectedProjectItem = ProjectItems.FirstOrDefault(i => i.Project == selected);
        _syncingSelection = false;
        OnPropertyChanged(nameof(InboxCount));
        OnPropertyChanged(nameof(NothingToShow));
        if (CurrentView is DashboardViewModel) Dashboard.Refresh();
    }

    /// <summary>After any list change: refreshes group pickers when the groups changed and lists empty groups too.</summary>
    private void SyncGroups()
    {
        var signature = string.Join("\n", State.Settings.Groups);
        if (signature != _groupsSignature)
        {
            _groupsSignature = signature;
            foreach (var vm in _projectViewModels.Values) vm.RefreshGroups();
        }
        if (SyncGroupNames()) ProjectsView.Refresh();
    }

    /// <summary>Every group from the settings gets a heading, even an empty one, except while searching.</summary>
    private bool SyncGroupNames()
    {
        List<string> names = SidebarFilter.IsSearching ? [] : State.Settings.Groups;
        if (_groupDescription.GroupNames.OfType<string>().SequenceEqual(names)) return false;
        _groupDescription.GroupNames.Clear();
        foreach (var name in names) _groupDescription.GroupNames.Add(name);
        return true;
    }

    /// <summary>Re-selects the open project in the sidebar after the filter hid and showed it again.</summary>
    private void SyncSelection()
    {
        _syncingSelection = true;
        SelectedProjectItem = CurrentProject is { IsInbox: false } current ? ProjectItems.FirstOrDefault(i => i.Project == current.Project) : null;
        _syncingSelection = false;
    }

    [RelayCommand]
    private void ToggleGroup(string? group)
    {
        if (string.IsNullOrEmpty(group)) return;
        var collapsed = State.Settings.CollapsedGroups;
        State.Settings.CollapsedGroups = collapsed.Contains(group, StringComparer.OrdinalIgnoreCase)
            ? collapsed.Where(g => !string.Equals(g, group, StringComparison.OrdinalIgnoreCase)).ToList()
            : [.. collapsed, group];
        State.MarkSettingsDirty();
        OnPropertyChanged(nameof(CollapsedGroups));
    }

    // ───────────── Discussions ─────────────

    /// <summary>The discussion shown in the side panel; null while it is closed.</summary>
    [ObservableProperty] private DiscussionViewModel? _discussion;

    public void OpenDiscussion(DiscussionTarget target) => Discussion = new DiscussionViewModel(this, State, target);

    public void CloseDiscussion() => Discussion = null;

    [RelayCommand]
    private void OpenGroupDiscussion(string? group)
    {
        if (!string.IsNullOrWhiteSpace(group)) OpenDiscussion(DiscussionTarget.ForGroup(group));
    }

    /// <summary>Comments per group name, for the sidebar headings.</summary>
    public IReadOnlyDictionary<string, int> GroupCommentCounts =>
        (State.Settings.GroupComments ?? []).ToDictionary(kv => kv.Key, kv => kv.Value.CountComments(), StringComparer.OrdinalIgnoreCase);

    public ProjectViewModel GetProjectViewModel(Project project)
    {
        if (!_projectViewModels.TryGetValue(project.Id, out var vm))
        {
            vm = new ProjectViewModel(State, this, project);
            _projectViewModels[project.Id] = vm;
        }
        return vm;
    }

    public ProjectViewModel OpenProject(Project project)
    {
        var vm = GetProjectViewModel(project);
        CurrentView = vm;
        IsDashboardActive = false;
        IsInboxActive = project.IsInbox;
        _syncingSelection = true;
        SelectedProjectItem = ProjectItems.FirstOrDefault(i => i.Project == project);
        _syncingSelection = false;
        State.AppSettings.LastProjectId = project.Id;
        return vm;
    }

    [RelayCommand]
    public void ShowDashboard()
    {
        Dashboard.Refresh();
        CurrentView = Dashboard;
        IsDashboardActive = true;
        IsInboxActive = false;
        _syncingSelection = true;
        SelectedProjectItem = null;
        _syncingSelection = false;
        State.AppSettings.LastProjectId = null;
    }

    [RelayCommand] private void ShowInbox() => OpenProject(State.Inbox);

    [RelayCommand]
    private void NewProject()
    {
        var name = InputDialog.Ask(Application.Current.MainWindow!, "New project", "Project name:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var group = CurrentProject is { IsInbox: false } current ? current.Project.Group : "Personal";
        var project = Project.Create(name, group);
        project.Categories.Add(new Category { Name = "GENERAL" });
        State.AddProject(project);
        OpenProject(project);
    }

    [RelayCommand]
    private void Import()
    {
        var window = new ImportWizardWindow(new ImportWizardViewModel(State)) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        if (CurrentView is DashboardViewModel) Dashboard.Refresh();
    }

    [RelayCommand]
    private void Export() =>
        new ExportDialog(State, CurrentProject?.Project) { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private void OpenSettings() => new SettingsWindow(State) { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private void Search() => new SearchWindow(State, this) { Owner = Application.Current.MainWindow }.Show();

    [RelayCommand] private void QuickCapture() => App.Current.ShowQuickCapture();

    [RelayCommand] private void SaveNow() => State.SaveNow();

    [RelayCommand]
    private void DeleteProject(ProjectListItemViewModel? item)
    {
        if (item == null) return;
        if (MessageBox.Show(Application.Current.MainWindow!,
                $"Delete project \"{item.Name}\"?\n\nIts file is moved to the backups folder, not erased.",
                "Delete project", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _projectViewModels.Remove(item.Project.Id);
        var wasCurrent = CurrentProject?.Project == item.Project;
        State.DeleteProject(item.Project);
        if (wasCurrent) ShowDashboard();
    }

    [RelayCommand]
    private void ArchiveProject(ProjectListItemViewModel? item)
    {
        if (item == null) return;
        var vm = GetProjectViewModel(item.Project);
        vm.Status = item.Project.Status == ProjectStatus.Archived ? ProjectStatus.Active : ProjectStatus.Archived;
    }

    [RelayCommand]
    private void RenameProject(ProjectListItemViewModel? item)
    {
        if (item == null) return;
        var name = InputDialog.Ask(Application.Current.MainWindow!, "Rename project", "Project name:", item.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        GetProjectViewModel(item.Project).Name = name.Trim();
    }

    [RelayCommand] private void NewGroup() => AskNewGroup();

    /// <summary>Asks for a group name until it is new (or the user cancels), adds the group and returns its name.</summary>
    private string? AskNewGroup()
    {
        var name = "";
        while (true)
        {
            name = InputDialog.Ask(Application.Current.MainWindow!, "New group", "Group name:", name);
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (GroupOperations.Validate(State.Settings, name) is not { } error) break;
            MessageBox.Show(Application.Current.MainWindow!, error, "New group", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        name = name.Trim();
        GroupOperations.Add(State.Settings, name);
        State.MarkSettingsDirty();
        State.NotifyListChanged();
        State.StatusText = $"Added the group {name}. Right-click a project › Move to group to fill it.";
        return name;
    }

    private static bool CanEditGroup(string? group) =>
        !string.IsNullOrWhiteSpace(group) && !WorkspaceSettings.IsDefaultGroup(group) && !ProjectGroups.Same(group, ProjectGroups.Archived);

    [RelayCommand(CanExecute = nameof(CanEditGroup))]
    private void RenameGroup(string? group)
    {
        if (!CanEditGroup(group)) return;
        var name = group!;
        while (true)
        {
            name = InputDialog.Ask(Application.Current.MainWindow!, "Rename group", "Group name:", name) ?? "";
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == group) return;
            if (GroupOperations.Validate(State.Settings, name, renaming: group) is not { } error) break;
            MessageBox.Show(Application.Current.MainWindow!, error, "Rename group", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        ApplyGroupChange(GroupOperations.Rename(State.Workspace, group!, name));
        if (Discussion is { Target.Kind: DiscussionKind.Group } open && ProjectGroups.Same(open.Target.Group, group))
            OpenDiscussion(DiscussionTarget.ForGroup(name));
        State.StatusText = $"Renamed the group {group} to {name.Trim()}.";
    }

    [RelayCommand(CanExecute = nameof(CanEditGroup))]
    private void DeleteGroup(string? group)
    {
        if (!CanEditGroup(group)) return;
        var count = State.Projects.Count(p => !p.IsInbox && ProjectGroups.Same(p.Group, group));
        var comments = State.Settings.CommentsFor(group!).CountComments();
        var moves = count == 1 ? "Its project moves to Personal." : $"Its {count} projects move to Personal.";
        var lost = comments == 1 ? "Its discussion (1 comment) is deleted." : $"Its discussion ({comments} comments) is deleted.";
        var details = string.Join(" ", new[] { count > 0 ? moves : null, comments > 0 ? lost : null }.OfType<string>());
        if (details.Length > 0 && MessageBox.Show(Application.Current.MainWindow!, $"Delete the group \"{group}\"?\n\n{details}",
                "Delete group", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ApplyGroupChange(GroupOperations.Delete(State.Workspace, group!));
        State.StatusText = count == 0 ? $"Deleted the group {group}." : $"Deleted the group {group}; its projects moved to Personal.";
    }

    private void ApplyGroupChange(List<Project> changedProjects)
    {
        foreach (var project in changedProjects) State.MarkDirty(project, touch: false);
        State.MarkSettingsDirty();
        OnPropertyChanged(nameof(CollapsedGroups));
        OnPropertyChanged(nameof(GroupCommentCounts));
        Discussion?.Sync();
        State.NotifyListChanged();
    }

    public List<GroupChoiceViewModel> GroupChoicesFor(Project project)
    {
        var current = string.IsNullOrWhiteSpace(project.Group) ? WorkspaceSettings.PersonalGroup : project.Group;
        return
        [
            .. State.Settings.Groups.Select(g => new GroupChoiceViewModel(g, ProjectGroups.Same(g, current), () => MoveToGroup(project, g))),
            new GroupChoiceViewModel(null, false, () =>
            {
                if (AskNewGroup() is { } group) MoveToGroup(project, group);
            }),
        ];
    }

    public void MoveToGroup(Project project, string group)
    {
        var current = string.IsNullOrWhiteSpace(project.Group) ? WorkspaceSettings.PersonalGroup : project.Group;
        if (ProjectGroups.Same(current, group)) return;
        project.Group = group;
        State.MarkDirty(project, touch: false);
        if (_projectViewModels.TryGetValue(project.Id, out var vm)) vm.RefreshGroups();
        State.NotifyListChanged();
        State.StatusText = $"Moved {project.Name} to {group}.";
    }

    [RelayCommand]
    private void HideProject(ProjectListItemViewModel? item)
    {
        if (item == null) return;
        SidebarFilter.SetShown([item.Project], false);
        State.StatusText = $"Hid {item.Name}. The eye button next to a filter box shows it again.";
    }

    public List<TemplateSourceViewModel> TemplateSourcesFor(Project target) =>
        State.Projects
            .Where(p => !p.IsInbox && p != target && p.Categories.Count > 0)
            .OrderBy(p => ProjectGroups.Order(p, State.Settings))
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new TemplateSourceViewModel(p, source => CopyCategories(target, source)))
            .ToList();

    /// <summary>Opens the target project and adds the source's categories it lacks (undoable there).</summary>
    public void CopyCategories(Project target, Project source)
    {
        var added = OpenProject(target).CopyCategoriesFrom(source);
        State.StatusText = added switch
        {
            0 => $"{target.Name} already has all of {source.Name}'s categories.",
            1 => $"Copied 1 category from {source.Name} (Ctrl+Z undoes it).",
            _ => $"Copied {added} categories from {source.Name} (Ctrl+Z undoes it).",
        };
    }

    [RelayCommand]
    private void ExportProject(ProjectListItemViewModel? item)
    {
        if (item != null) new ExportDialog(State, item.Project) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    public void Navigate(SearchHit hit)
    {
        var vm = OpenProject(hit.Project);
        if (hit.Item != null) vm.Reveal(hit.Item);
        else if (hit.Note != null) vm.RevealNote(hit.Note);
    }
}
