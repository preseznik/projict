using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Features;

namespace ProJict.App.ViewModels;

/// <summary>A choice in the Feed's "what" filter; Kinds is null for everything.</summary>
public sealed record FeedKindOption(string Label, FeedKind[]? Kinds)
{
    public override string ToString() => Label;
}

/// <summary>A choice in the Feed's period filter; Days is null for all time, 0 for today.</summary>
public sealed record FeedPeriodOption(string Label, int? Days)
{
    public override string ToString() => Label;
}

/// <summary>The Feed: a timeline of everything that happened in ProJict, filtered and searched, each entry leading to its item.</summary>
public sealed partial class FeedViewModel : ObservableObject
{
    public const string AllProjects = "All projects";
    public const string Everyone = "Everyone";
    public const string People = "People";
    public const string Agents = "AI agents";
    private const int PageSize = 200;

    private readonly AppState _state;
    private readonly MainViewModel _main;
    private List<FeedEvent> _all = [];
    private int _limit = PageSize;

    public FeedViewModel(AppState state, MainViewModel main)
    {
        _state = state;
        _main = main;
    }

    public static IReadOnlyList<FeedKindOption> KindOptions { get; } =
    [
        new("Everything", null),
        new("Added & finished", [FeedKind.Added, FeedKind.Done]),
        new("Comments", [FeedKind.Comment]),
        new("History (check-outs, reviews…)", [FeedKind.History]),
        new("Releases, shipping & notes", [FeedKind.Release, FeedKind.Ship, FeedKind.Note]),
        new("Projects", [FeedKind.Project]),
        new("API tokens", [FeedKind.Api]),
    ];

    public static IReadOnlyList<FeedPeriodOption> Periods { get; } =
    [
        new("Today", 0),
        new("Last 7 days", 7),
        new("Last 30 days", 30),
        new("All time", null),
    ];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _projectFilter = AllProjects;
    [ObservableProperty] private FeedKindOption _kindFilter = KindOptions[0];
    [ObservableProperty] private string _whoFilter = Everyone;
    [ObservableProperty] private FeedPeriodOption _period = Periods[1];

    public List<string> ProjectChoices { get; private set; } = [AllProjects];
    public List<string> WhoChoices { get; private set; } = [Everyone, People, Agents];
    public List<FeedRowViewModel> Rows { get; private set; } = [];
    public bool HasMore { get; private set; }
    public string Summary { get; private set; } = "";
    public bool IsFiltered => SearchText.Trim().Length > 0 || ProjectFilter != AllProjects || KindFilter != KindOptions[0] || WhoFilter != Everyone;
    public string EmptyText => _all.Count == 0
        ? "Nothing has happened yet. Items you add and finish, comments, and what AI agents do will show up here."
        : "Nothing matches. Try a longer period or clear the filters.";

    partial void OnSearchTextChanged(string value) => Reapply();
    partial void OnProjectFilterChanged(string value) => Reapply();
    partial void OnKindFilterChanged(FeedKindOption value) => Reapply();
    partial void OnWhoFilterChanged(string value) => Reapply();
    partial void OnPeriodChanged(FeedPeriodOption value) => Reapply();

    private void Reapply()
    {
        _limit = PageSize;
        Apply();
    }

    /// <summary>Rebuilds the timeline from the workspace (on open, and when something changes while it shows).</summary>
    public void Refresh()
    {
        List<ProJict.Core.Api.ApiToken> tokens;
        lock (_main.ApiStore.Sync) tokens = [.. _main.ApiStore.Data.Tokens];
        _all = [.. ActivityFeed.Build(_state.Projects, _state.Settings, _state.UserName)
            .Concat(ActivityFeed.ApiTokens(tokens, DateTime.Now, _state.UserName)).OrderByDescending(e => e.At)];
        ProjectChoices = [AllProjects, .. _state.Projects.Select(p => p.Name).Order(StringComparer.CurrentCultureIgnoreCase)];
        WhoChoices = [Everyone, People, Agents, .. _all.Select(e => e.Who).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
        OnPropertyChanged(nameof(ProjectChoices));
        OnPropertyChanged(nameof(WhoChoices));
        if (!ProjectChoices.Contains(ProjectFilter)) ProjectFilter = AllProjects;
        if (!WhoChoices.Contains(WhoFilter)) WhoFilter = Everyone;
        Apply();
    }

    private void Apply()
    {
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var since = Period.Days switch { null => DateTime.MinValue, 0 => DateTime.Today, var days => DateTime.Now.AddDays(-days.Value) };
        var matches = _all.Where(e =>
            e.At >= since &&
            (ProjectFilter == AllProjects || string.Equals(e.Project?.Name, ProjectFilter, StringComparison.OrdinalIgnoreCase)) &&
            (KindFilter.Kinds == null || KindFilter.Kinds.Contains(e.Kind)) &&
            WhoFilter switch
            {
                Everyone => true,
                People => !e.Agent,
                Agents => e.Agent,
                var name => string.Equals(e.Who, name, StringComparison.OrdinalIgnoreCase),
            } &&
            terms.All(t => e.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();

        DateTime? day = null;
        Rows = matches.Take(_limit).Select(e =>
        {
            var header = day != e.At.Date ? DayName(e.At) : null;
            day = e.At.Date;
            return new FeedRowViewModel(e, header, this);
        }).ToList();
        HasMore = matches.Count > _limit;
        Summary = matches.Count == _all.Count && Period.Days == null
            ? $"{_all.Count} events"
            : $"{matches.Count} of {_all.Count} events";
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(EmptyText));
    }

    private static string DayName(DateTime at) => at.Date == DateTime.Today ? "Today"
        : at.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : at.ToString(at.Year == DateTime.Today.Year ? "dddd d MMMM" : "dddd d MMMM yyyy", CultureInfo.CurrentCulture);

    [RelayCommand]
    private void ShowMore()
    {
        _limit += PageSize;
        Apply();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        ProjectFilter = AllProjects;
        KindFilter = KindOptions[0];
        WhoFilter = Everyone;
    }

    /// <summary>
    /// Goes to what the entry is about: the item, the release, the note or the project. A comment opens its discussion
    /// beside the Feed instead, pointing it out, so you keep your place in the Feed.
    /// </summary>
    public void Open(FeedEvent e)
    {
        if (e.Kind == FeedKind.Api)
        {
            _main.OpenApiCommand.Execute(null);
            return;
        }
        if (e.Group != null || e is { DiscussionTargetId: not null, Project: not null })
        {
            _main.OpenDiscussion(e.Group != null ? DiscussionTarget.ForGroup(e.Group) : DiscussionTarget.InProject(e.Project!, e.DiscussionTargetId!.Value));
            if (e.CommentId is { } comment) _main.Discussion?.Highlight(comment);
            return;
        }
        if (e.Project == null) return;
        var project = _main.OpenProject(e.Project);
        if (e.Item != null) project.Reveal(e.Item);
        if (e.DiscussionTargetId is { } target && project.HasDiscussionTarget(target)) project.OpenDiscussion(target);
        if (e.Release != null)
        {
            project.SelectedTab = 2;
            project.SelectedRelease = project.Releases.FirstOrDefault(r => r.Id == e.Release.Id);
        }
        if (e.Note != null) project.RevealNote(e.Note);
        if (e.Ship != null) project.RevealShip(e.Ship);
    }

    public void OpenProject(FeedEvent e)
    {
        if (e.Group != null) _main.OpenDiscussion(DiscussionTarget.ForGroup(e.Group));
        else if (e.Project != null) _main.OpenProject(e.Project);
    }
}

/// <summary>One entry in the Feed, with the day heading above it when it starts a new day.</summary>
public sealed partial class FeedRowViewModel(FeedEvent e, string? dayHeader, FeedViewModel owner)
{
    public FeedEvent Event { get; } = e;
    public string? DayHeader { get; } = dayHeader;
    public string Time => Event.At.ToString("HH:mm", CultureInfo.CurrentCulture);
    public string TimeToolTip => Event.At.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public string Who => Event.Who;
    public bool IsAgent => Event.Agent;
    public string Action => Event.Action;
    public string Subject => Event.Subject.Replace("\r\n", " ").Replace('\n', ' ');
    public string? Detail => Event.Detail?.Replace("\r\n", " ").Replace('\n', ' ');
    /// <summary>Where it happened: "ProJict › FEATURES", or the group.</summary>
    public string Where => Event.Group != null ? $"{Event.Group} group"
        : Event.Category != null ? $"{Event.Project?.Name} › {Event.Category.Name}"
        : Event.Project?.Name ?? "";

    public string KindGlyph => Event.Kind switch
    {
        FeedKind.Added => "",
        FeedKind.Done => "",
        FeedKind.Comment => "",
        FeedKind.Release => "",
        FeedKind.Note => "",
        FeedKind.Ship => "",
        FeedKind.Project => "",
        FeedKind.Api => "",
        _ => Event.Agent ? "" : "",
    };

    [RelayCommand] private void Open() => owner.Open(Event);
    [RelayCommand] private void OpenProject() => owner.OpenProject(Event);
}
