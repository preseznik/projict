using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.App.ViewModels;

/// <summary>How projects are grouped in the sidebar and in the show/hide dropdowns.</summary>
public static class ProjectGroups
{
    public const string Archived = WorkspaceSettings.ArchivedGroup;

    public static string Of(Project project) => project.Status == ProjectStatus.Archived ? Archived
        : string.IsNullOrWhiteSpace(project.Group) ? WorkspaceSettings.PersonalGroup : project.Group.Trim();

    public static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Groups follow the order in the workspace settings; archived projects come last.</summary>
    public static int Order(Project project, WorkspaceSettings settings)
    {
        if (project.Status == ProjectStatus.Archived) return int.MaxValue;
        var group = Of(project);
        var i = settings.Groups.FindIndex(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 1000 : i;
    }
}

/// <summary>One project in the show/hide dropdown.</summary>
public sealed class ProjectToggleViewModel(Project project, ProjectFilterViewModel owner) : ObservableObject
{
    public Project Project { get; } = project;
    public string Name => Project.Name;

    public bool IsShown
    {
        get => !owner.IsHidden(Project);
        set => owner.SetShown([Project], value);
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsShown));
}

/// <summary>A group in the show/hide dropdown; its check box shows or hides the whole group.</summary>
public sealed class ProjectToggleGroupViewModel(string name, List<ProjectToggleViewModel> items, ProjectFilterViewModel owner) : ObservableObject
{
    public string Name { get; } = name;
    public List<ProjectToggleViewModel> Items { get; } = items;

    public bool? AllShown
    {
        get => Items.All(i => i.IsShown) ? true : Items.Any(i => i.IsShown) ? null : false;
        set => owner.SetShown(Items.Select(i => i.Project), value == true);
    }

    internal void Refresh()
    {
        foreach (var item in Items) item.Refresh();
        OnPropertyChanged(nameof(AllShown));
    }
}

/// <summary>Search text plus a saved list of hidden projects; the sidebar and the dashboard each have one.</summary>
public sealed partial class ProjectFilterViewModel(AppState state, List<Guid> hidden) : ObservableObject
{
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private string _signature = "";

    /// <summary>Raised when the search text or the hidden projects change.</summary>
    public event Action? Changed;

    /// <summary>Raised on Enter in the search box.</summary>
    public event Action? Accepted;

    [ObservableProperty] private string _searchText = "";

    public List<ProjectToggleGroupViewModel> Groups { get; private set; } = [];
    public int HiddenCount => Groups.Sum(g => g.Items.Count(i => !i.IsShown));
    public bool HasHidden => HiddenCount > 0;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        Changed?.Invoke();
    }

    public bool IsHidden(Project project) => hidden.Contains(project.Id);

    public bool Matches(Project project) => !IsHidden(project) && MatchesSearch(project);

    public bool MatchesSearch(Project project)
    {
        var text = SearchText.Trim();
        return text.Length == 0 || Contains(project.Name, text) || Contains(project.Key, text);

        // Case- and accent-insensitive, so "zaba" finds "Žaba".
        static bool Contains(string? s, string text) =>
            s != null && Compare.IndexOf(s, text, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    /// <summary>Rebuilds the dropdown; skipped when no project was added, removed, renamed or regrouped, so an open dropdown keeps its place.</summary>
    public void Rebuild(IEnumerable<Project> projects)
    {
        var list = projects.ToList();
        var signature = string.Join("\n", list.Select(p => $"{p.Id}\t{p.Name}\t{ProjectGroups.Of(p)}"));
        if (signature == _signature) return;
        _signature = signature;

        Groups = list
            .GroupBy(ProjectGroups.Of, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => ProjectGroups.Order(g.First(), state.Settings))
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new ProjectToggleGroupViewModel(g.Key,
                g.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => new ProjectToggleViewModel(p, this)).ToList(),
                this))
            .ToList();
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHidden));
    }

    public void SetShown(IEnumerable<Project> projects, bool shown)
    {
        foreach (var project in projects)
        {
            if (shown) hidden.Remove(project.Id);
            else if (!hidden.Contains(project.Id)) hidden.Add(project.Id);
        }
        state.MarkSettingsDirty();
        foreach (var group in Groups) group.Refresh();
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHidden));
        Changed?.Invoke();
    }

    [RelayCommand] private void ShowAll() => SetShown(Groups.SelectMany(g => g.Items).Select(i => i.Project), true);

    [RelayCommand] private void HideAll() => SetShown(Groups.SelectMany(g => g.Items).Select(i => i.Project), false);

    [RelayCommand] private void Accept() => Accepted?.Invoke();

    [RelayCommand] private void ClearSearch() => SearchText = "";
}
