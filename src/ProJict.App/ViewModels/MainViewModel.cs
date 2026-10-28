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

public sealed partial class ProjectListItemViewModel(Project project, WorkspaceSettings settings) : ObservableObject
{
    public Project Project { get; } = project;
    public string Name => Project.Name;
    public ProjectStatus Status => Project.Status;
    public string DisplayGroup => ProjectGroups.Of(Project);
    public int GroupOrder => ProjectGroups.Order(Project, settings);
    public int OpenCount => Project.Categories.Sum(c => c.Items.Count(i => !i.IsDone));

    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Used as the accessible name of the sidebar entry.</summary>
    public override string ToString() => Name;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<Guid, ProjectViewModel> _projectViewModels = [];
    private bool _syncingSelection;

    public MainViewModel(AppState state)
    {
        State = state;
        Updates = new Services.UpdateService(state);
        Dashboard = new DashboardViewModel(state, this);
        ProjectsView = CollectionViewSource.GetDefaultView(ProjectItems);
        ProjectsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProjectListItemViewModel.DisplayGroup)));
        ProjectsView.SortDescriptions.Add(new SortDescription(nameof(ProjectListItemViewModel.GroupOrder), ListSortDirection.Ascending));
        ProjectsView.SortDescriptions.Add(new SortDescription(nameof(ProjectListItemViewModel.Name), ListSortDirection.Ascending));
        SidebarFilter = new ProjectFilterViewModel(state, state.Settings.SidebarHiddenProjects);
        ProjectsView.Filter = o => o is ProjectListItemViewModel i && SidebarFilter.Matches(i.Project);
        SidebarFilter.Changed += () =>
        {
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
        };

        var last = state.AppSettings.LastProjectId is { } id ? state.Projects.FirstOrDefault(p => p.Id == id) : null;
        if (last != null) OpenProject(last);
        else ShowDashboard();
    }

    public AppState State { get; }
    public Services.UpdateService Updates { get; }
    public DashboardViewModel Dashboard { get; }
    public ObservableCollection<ProjectListItemViewModel> ProjectItems { get; } = [];
    public ICollectionView ProjectsView { get; }
    public ProjectFilterViewModel SidebarFilter { get; }
    public bool NothingToShow => ProjectItems.Count > 0 && ProjectsView.IsEmpty;

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
            ProjectItems.Add(new ProjectListItemViewModel(project, State.Settings));
        SelectedProjectItem = ProjectItems.FirstOrDefault(i => i.Project == selected);
        _syncingSelection = false;
        OnPropertyChanged(nameof(InboxCount));
        OnPropertyChanged(nameof(NothingToShow));
        if (CurrentView is DashboardViewModel) Dashboard.Refresh();
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
