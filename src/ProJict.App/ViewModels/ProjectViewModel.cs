using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.App.Views;
using ProJict.Core.Export;
using ProJict.Core.Features;
using ProJict.Core.Import;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.App.ViewModels;

/// <summary>
/// The project page. Every structural edit goes through <see cref="Mutate"/>: snapshot for undo,
/// change the model, autosave, rebuild the affected categories and restore keyboard focus.
/// </summary>
public sealed partial class ProjectViewModel : ObservableObject
{
    private const int MaxUndo = 100;
    private const string AllTags = "All tags";
    private const string NoTag = "No tag";

    private readonly AppState _state;
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private bool _textEditOpen;
    private Guid? _focusedItemId;

    public ProjectViewModel(AppState state, MainViewModel main, Project project)
    {
        _state = state;
        Main = main;
        Project = project;
        _sideBySide = state.Settings.SideBySide;
        _sortByPriority = state.Settings.SortByPriority;
        RebuildAll();
        ReloadSideLists();
        state.ProjectReplaced += p =>
        {
            if (p != Project) return;
            RebuildAll();
            ReloadSideLists();
            NotifyDetails();
        };
    }

    public Project Project { get; }
    public MainViewModel Main { get; }
    public ObservableCollection<CategoryViewModel> Categories { get; } = [];
    public ObservableCollection<NoteViewModel> Notes { get; } = [];
    public ObservableCollection<Release> Releases { get; } = [];
    public ObservableCollection<ArchivedFile> Archive { get; } = [];

    public static IReadOnlyList<string> TagChoices { get; } = ["BUG", "ADD", "ISSUE", "CHANGE", "FIX", "IDEA"];
    public static IReadOnlyList<ProjectStatus> Statuses { get; } = Enum.GetValues<ProjectStatus>();
    /// <summary>A copy, so raising Groups gives the header's group box a fresh list.</summary>
    public IReadOnlyList<string> Groups => [.. _state.Settings.Groups];
    public IReadOnlyList<string> TagFilters { get; private set; } = [AllTags];
    public static IReadOnlyList<StateFilterOption> StateFilters { get; } =
    [
        new("All states", null),
        new("To do", ItemState.Todo),
        new("In progress", ItemState.InProgress),
        new("Done", ItemState.Done),
        new("Needs review", ItemState.Done, ReviewOnly: true),
    ];
    public static IReadOnlyList<PriorityFilterOption> PriorityFilters { get; } =
    [
        new("All priorities", null),
        new("Urgent", Priority.Urgent),
        new("High and up", Priority.High),
        new("Medium and up", Priority.Medium),
        new("Any priority", Priority.Low),
        new("No priority", null, NoneOnly: true),
    ];
    public static IReadOnlyList<Priority> PriorityChoices { get; } = [Priority.Urgent, Priority.High, Priority.Medium, Priority.Low];
    public bool IsInbox => Project.IsInbox;
    public bool IsNotInbox => !Project.IsInbox;

    /// <summary>Asks the view to focus an item's text box; caret -1 means end of text.</summary>
    public event Action<Guid, int>? FocusRequested;
    public event Action<CategoryViewModel>? FocusAddItemRequested;
    /// <summary>Set by the board: the caret position in an item's text box, so agent edits can put it back.</summary>
    public Func<Guid, int?>? CaretOf { get; set; }

    // ───────────── Details ─────────────

    public string Name
    {
        get => Project.Name;
        set => SetDetail(Project.Name, value, v => Project.Name = v ?? "", listChanged: true);
    }

    public string Key
    {
        get => Project.Key;
        set => SetDetail(Project.Key, value, v => Project.Key = Project.KeyFromName(v ?? ""));
    }

    public string FilePrefix
    {
        get => Project.FilePrefix;
        set => SetDetail(Project.FilePrefix, value, v => Project.FilePrefix = string.IsNullOrWhiteSpace(v) ? "TODO" : v.Trim().ToUpperInvariant());
    }

    public string Group
    {
        get => Project.Group;
        set
        {
            if (ProjectGroups.Same(value, ProjectGroups.Archived))
            {
                RefreshGroups(); // "Archived" is the status, not a group
                return;
            }
            // A name typed here that isn't a group yet becomes one (before the sidebar rebuilds).
            if (!string.IsNullOrWhiteSpace(value) && GroupOperations.Validate(_state.Settings, value) == null)
            {
                _state.Settings.Groups.Add(value.Trim());
                _state.MarkSettingsDirty();
            }
            SetDetail(Project.Group, value, v => Project.Group = v?.Trim() ?? "", listChanged: true);
        }
    }

    public ProjectStatus Status
    {
        get => Project.Status;
        set
        {
            if (value == Project.Status) return;
            Project.Status = value;
            Touch();
            OnPropertyChanged();
            _state.NotifyListChanged();
        }
    }

    public string? RepoPath { get => Project.RepoPath; set => SetDetail(Project.RepoPath, value, v => Project.RepoPath = Blank(v)); }
    public string? GitHubUrl { get => Project.GitHubUrl; set => SetDetail(Project.GitHubUrl, value, v => Project.GitHubUrl = Blank(v)); }
    public string? PromptPreamble { get => Project.PromptPreamble; set => SetDetail(Project.PromptPreamble, value, v => Project.PromptPreamble = Blank(v)); }
    public string? Description { get => Project.Description; set => SetDetail(Project.Description, value, v => Project.Description = Blank(v)); }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    public string ExportNamesText => $"→ {TxtExporter.TodoFileName(Project)}, {TxtExporter.DoneFileName(Project)}";

    private void SetDetail(string? current, string? value, Action<string?> apply, bool listChanged = false, [CallerMemberName] string? property = null)
    {
        if (current == value) return;
        apply(value);
        Touch();
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(ExportNamesText));
        if (listChanged) _state.NotifyListChanged();
    }

    /// <summary>After the GitHub check linked this project to a repository.</summary>
    public void RefreshGitHubLink() => OnPropertyChanged(nameof(GitHubUrl));

    /// <summary>After groups were added, renamed or deleted elsewhere.</summary>
    public void RefreshGroups()
    {
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(Group));
    }

    private void NotifyDetails()
    {
        foreach (var p in new[] { nameof(Name), nameof(Key), nameof(FilePrefix), nameof(Group), nameof(Status), nameof(RepoPath), nameof(GitHubUrl), nameof(PromptPreamble), nameof(Description) })
            OnPropertyChanged(p);
    }

    // ───────────── Board state ─────────────

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _tagFilter = AllTags;
    [ObservableProperty] private StateFilterOption _stateFilter = StateFilters[0];
    [ObservableProperty] private bool _showDone;
    [ObservableProperty] private bool _sideBySide;
    [ObservableProperty] private int _selectedTab;

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnTagFilterChanged(string value) => ApplyFilter();
    partial void OnStateFilterChanged(StateFilterOption value) => ApplyFilter();

    partial void OnShowDoneChanged(bool value)
    {
        foreach (var c in Categories) c.IsDoneExpanded = value;
    }

    partial void OnSideBySideChanged(bool value)
    {
        _state.Settings.SideBySide = value;
        _state.MarkSettingsDirty();
        foreach (var c in Categories) c.RaiseShowDone();
    }

    // ───────────── Priority ─────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityButtonText), nameof(IsPriorityViewActive))]
    private PriorityFilterOption _priorityFilter = PriorityFilters[0];

    /// <summary>Open items are listed most pressing first (only on screen: their own order, used by exports, stays).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityButtonText), nameof(IsPriorityViewActive))]
    private bool _sortByPriority;

    partial void OnPriorityFilterChanged(PriorityFilterOption value) => ApplyFilter();

    partial void OnSortByPriorityChanged(bool value)
    {
        _state.Settings.SortByPriority = value;
        _state.MarkSettingsDirty();
        foreach (var c in Categories) c.Refresh();
        ApplyFilter();
    }

    public bool IsPriorityViewActive => SortByPriority || PriorityFilter is { IsActive: true };

    /// <summary>The toolbar button's label: what the board is filtered or sorted by.</summary>
    public string PriorityButtonText
    {
        get
        {
            var filter = PriorityFilter is { IsActive: true } f ? f.Label : null;
            return filter != null ? SortByPriority ? $"{filter}, sorted" : filter : SortByPriority ? "By priority" : "Priority";
        }
    }

    public void SetPriority(ItemRowViewModel row, Priority priority)
    {
        if (row.Item.Priority == priority) return;
        // Redrawn rather than refreshed: when sorting by priority the item moves.
        Mutate(() => row.Item.Priority = priority, [row.Category], row.Item.Id);
        _state.StatusText = priority == Priority.None ? "Priority removed." : $"Priority set to {priority}.";
    }

    /// <summary>Reordering by hand doesn't mix with a board sorted by priority; true (with a hint) when it's sorted.</summary>
    private bool SortedByPriority()
    {
        if (!SortByPriority) return false;
        _state.StatusText = "The board is sorted by priority; turn that off (Priority › Sort by priority) to reorder items.";
        return true;
    }

    public bool IsFilterActive => !string.IsNullOrWhiteSpace(FilterText) || (TagFilter ?? AllTags) != AllTags || StateFilter?.State != null ||
                                  WorkedBy != null || PriorityFilter is { IsActive: true };

    public void Touch() => _state.MarkDirty(Project);

    /// <summary>How you sign notes, comments and history entries.</summary>
    public string UserName => _state.UserName;

    /// <summary>True when every category is folded; the board's toggle then offers "Expand all".</summary>
    public bool AllCollapsed => Categories.Count > 0 && Categories.All(c => !c.IsExpanded);
    public string ToggleAllText => AllCollapsed ? "Expand all" : "Collapse all";
    public string ToggleAllGlyph => AllCollapsed ? "\uE70D" : "\uE70E";

    [RelayCommand]
    private void ToggleAllCategories()
    {
        var expand = AllCollapsed;
        foreach (var category in Categories) category.IsExpanded = expand;
    }

    public void OnCategoryExpandedChanged()
    {
        OnPropertyChanged(nameof(AllCollapsed));
        OnPropertyChanged(nameof(ToggleAllText));
        OnPropertyChanged(nameof(ToggleAllGlyph));
    }

    public void RebuildAll()
    {
        var existing = Categories.ToDictionary(c => c.Model);
        var list = Project.Categories.Select(c => existing.TryGetValue(c, out var vm) ? vm : new CategoryViewModel(this, c)).ToList();
        foreach (var vm in list.Where(v => existing.ContainsKey(v.Model))) vm.Refresh();
        if (!Categories.SequenceEqual(list))
        {
            Categories.Clear();
            foreach (var vm in list) Categories.Add(vm);
        }
        TagFilters = [AllTags, NoTag, .. TagChoices.Union(Project.Walk().Select(l => l.Item.Tag).OfType<string>(), StringComparer.OrdinalIgnoreCase)];
        OnPropertyChanged(nameof(TagFilters));
        ApplyFilter();
        OnCategoryExpandedChanged();
    }

    public void ApplyFilter()
    {
        var active = IsFilterActive;
        var terms = (FilterText ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tag = TagFilter ?? AllTags;
        var state = StateFilter?.State;
        var reviewOnly = StateFilter?.ReviewOnly == true;

        bool StateOk(TodoItem item) => (state == null || item.State == state) && (!reviewOnly || item.NeedsReview);

        bool Matches(TodoItem item)
        {
            var tagOk = tag == AllTags || (tag == NoTag ? string.IsNullOrEmpty(item.Tag) : string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase));
            var text = item.SearchText();
            return tagOk && StateOk(item) && PriorityFilter?.Matches(item) != false && terms.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)) &&
                   (WorkedBy == null || item.Contributors().Any(c => string.Equals(c.Name, WorkedBy, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var category in Categories)
        {
            if (!active)
            {
                foreach (var row in category.AllRows) row.IsVisible = true;
                category.IsVisible = true;
                category.RaiseShowDone();
                continue;
            }

            // A match keeps its ancestors and descendants (those in the chosen state) visible so the tree still reads correctly.
            var parents = new Dictionary<TodoItem, TodoItem>();
            var matches = new List<TodoItem>();
            foreach (var loc in category.Model.Walk())
            {
                if (loc.Parent != null) parents[loc.Item] = loc.Parent;
                if (Matches(loc.Item)) matches.Add(loc.Item);
            }
            var visible = new HashSet<TodoItem>();
            foreach (var item in matches.SelectMany(m => m.Descendants().Where(StateOk).Prepend(m)))
                for (TodoItem? p = item; p != null && visible.Add(p); p = parents.GetValueOrDefault(p)) { }
            var nameMatches = terms.Length > 0 && tag == AllTags && state == null && WorkedBy == null && PriorityFilter is not { IsActive: true } && terms.All(t => category.Name.Contains(t, StringComparison.OrdinalIgnoreCase));
            foreach (var row in category.AllRows) row.IsVisible = nameMatches || visible.Contains(row.Item);
            category.IsVisible = nameMatches || visible.Count > 0;
            category.RaiseShowDone();
        }
        OnPropertyChanged(nameof(IsFilterActive));
    }

    public List<ItemRowViewModel> VisibleRows()
    {
        var rows = new List<ItemRowViewModel>();
        foreach (var category in Categories.Where(c => c.IsVisible && c.IsExpanded))
        {
            rows.AddRange(category.OpenRows.Where(r => r.IsVisible));
            if (category.ShowDoneRows) rows.AddRange(category.DoneRows.Where(r => r.IsVisible));
        }
        return rows;
    }

    // ───────────── Focus & text editing ─────────────

    public ItemRowViewModel? FocusedRow { get; private set; }

    public void OnRowGotFocus(ItemRowViewModel row)
    {
        FocusedRow = row;
        _focusedItemId = row.Item.Id;
        _textEditOpen = false;
    }

    public void OnRowLostFocus(ItemRowViewModel row)
    {
        if (_focusedItemId == row.Item.Id) _focusedItemId = null;
        _textEditOpen = false;
        var item = row.Item;

        // "BUG: crash" typed into the text becomes a tag.
        if (string.IsNullOrEmpty(item.Tag))
        {
            var (tag, text) = TxtListParser.SplitTag(item.Text);
            if (tag != null && text.Length > 0)
            {
                Checkpoint();
                item.Tag = tag;
                item.Text = text;
                Touch();
                row.Refresh();
                RefreshTagFilters();
            }
        }

        // Empty rows left behind (Enter pressed, nothing typed) are removed once focus has really moved on.
        if (item.Text.Trim().Length == 0 && item.Children.Count == 0)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (_focusedItemId == item.Id || item.Text.Trim().Length > 0 || item.Children.Count > 0) return;
                if (Project.Find(item.Id) is not { } loc) return;
                loc.Siblings.Remove(item);
                Touch();
                CategoryFor(loc.Category)?.Refresh();
                ApplyFilter();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    public void OnHeaderGotFocus() => _textEditOpen = false;

    /// <summary>Called before a text field changes; the first change after focusing records one undo step.</summary>
    public void BeforeTextChange()
    {
        if (_textEditOpen) return;
        Push(_undo, Json.Serialize(Project));
        _redo.Clear();
        _textEditOpen = true;
        NotifyUndo();
    }

    private void RefreshTagFilters()
    {
        TagFilters = [AllTags, NoTag, .. TagChoices.Union(Project.Walk().Select(l => l.Item.Tag).OfType<string>(), StringComparer.OrdinalIgnoreCase)];
        OnPropertyChanged(nameof(TagFilters));
    }

    // ───────────── Undo ─────────────

    public void Checkpoint()
    {
        Push(_undo, Json.Serialize(Project));
        _redo.Clear();
        _textEditOpen = false;
        NotifyUndo();
    }

    private static void Push(List<string> stack, string snapshot)
    {
        stack.Add(snapshot);
        if (stack.Count > MaxUndo) stack.RemoveAt(0);
    }

    private static string Pop(List<string> stack)
    {
        var s = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return s;
    }

    private bool CanUndo() => _undo.Count > 0;
    private bool CanRedo() => _redo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo()
    {
        if (_undo.Count == 0) return;
        Push(_redo, Json.Serialize(Project));
        Restore(Pop(_undo));
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public void Redo()
    {
        if (_redo.Count == 0) return;
        Push(_undo, Json.Serialize(Project));
        Restore(Pop(_redo));
    }

    private void Restore(string json)
    {
        var focus = _focusedItemId;
        Project.RestoreFrom(Json.Deserialize<Project>(json)!);
        _textEditOpen = false;
        Categories.Clear();
        RebuildAll();
        ReloadSideLists();
        NotifyDetails();
        Touch();
        _state.NotifyListChanged();
        NotifyUndo();
        if (focus != null && Project.Find(focus.Value) != null) FocusRequested?.Invoke(focus.Value, -1);
    }

    private void NotifyUndo()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // ───────────── Structural edits ─────────────

    private void Mutate(Action change, IEnumerable<CategoryViewModel?> affected, Guid? focus = null, int caret = -1)
    {
        var hadFocus = _focusedItemId;
        Checkpoint();
        change();
        Touch();
        foreach (var c in affected.OfType<CategoryViewModel>().Distinct()) c.Refresh();
        ApplyFilter();
        var target = focus ?? hadFocus;
        if (target != null && Project.Find(target.Value) != null) FocusRequested?.Invoke(target.Value, caret);
    }

    private CategoryViewModel? CategoryFor(Category category) => Categories.FirstOrDefault(c => c.Model == category);

    private static List<TodoItem> SiblingsOf(ItemRowViewModel row) => row.Parent?.Children ?? row.Category.Model.Items;

    public void AddItem(CategoryViewModel category, string text)
    {
        var item = TxtListParser.ParseLine(text);
        if (item == null) return;
        Mutate(() => category.Model.Items.Add(item), [category]);
        RefreshTagFilters();
    }

    public void InsertSibling(ItemRowViewModel row, bool before)
    {
        // On a board sorted by priority, a new item with the same priority shows up right next to this one.
        var item = new TodoItem { Priority = SortByPriority && !row.InDoneSection ? row.Item.Priority : Priority.None };
        Mutate(() =>
        {
            var list = SiblingsOf(row);
            if (row.InDoneSection && row.Parent == null) row.Category.Model.Items.Add(item);
            else list.Insert(list.IndexOf(row.Item) + (before ? 0 : 1), item);
        }, [row.Category], before ? row.Item.Id : item.Id, before ? 0 : -1);
    }

    public void AddChild(ItemRowViewModel row)
    {
        var item = new TodoItem();
        Mutate(() => row.Item.Children.Add(item), [row.Category], item.Id);
    }

    public void Indent(ItemRowViewModel row, int caret)
    {
        if (SortedByPriority()) return;
        var list = SiblingsOf(row);
        var index = list.IndexOf(row.Item);
        TodoItem? previous = null;
        for (var i = index - 1; i >= 0; i--)
        {
            // Top-level TODO and DONE items share one list; only indent under a neighbour from the same section.
            if (row.Parent != null || list[i].IsDone == row.Item.IsDone)
            {
                previous = list[i];
                break;
            }
        }
        if (previous == null) return;
        Mutate(() =>
        {
            list.Remove(row.Item);
            previous.Children.Add(row.Item);
        }, [row.Category], row.Item.Id, caret);
    }

    public void Outdent(ItemRowViewModel row, int caret)
    {
        if (row.Parent == null || Project.Find(row.Parent.Id) is not { } parentLoc) return;
        Mutate(() =>
        {
            row.Parent.Children.Remove(row.Item);
            parentLoc.Siblings.Insert(parentLoc.Siblings.IndexOf(row.Parent) + 1, row.Item);
        }, [row.Category], row.Item.Id, caret);
    }

    public void Move(ItemRowViewModel row, int delta, int caret)
    {
        if (SortedByPriority()) return;
        var list = SiblingsOf(row);
        var index = list.IndexOf(row.Item);
        var target = index + delta;
        while (target >= 0 && target < list.Count && row.Parent == null && list[target].IsDone != row.Item.IsDone) target += delta;

        if (target >= 0 && target < list.Count)
        {
            Mutate(() =>
            {
                list.RemoveAt(index);
                list.Insert(target, row.Item);
            }, [row.Category], row.Item.Id, caret);
            return;
        }

        // At the edge of a category, a top-level item hops into the neighbouring category.
        if (row.Parent != null) return;
        var categoryIndex = Categories.IndexOf(row.Category);
        var neighbour = categoryIndex + delta;
        if (neighbour < 0 || neighbour >= Categories.Count) return;
        var other = Categories[neighbour];
        Mutate(() =>
        {
            list.Remove(row.Item);
            if (delta < 0) other.Model.Items.Add(row.Item);
            else other.Model.Items.Insert(0, row.Item);
            other.IsExpanded = true;
        }, [row.Category, other], row.Item.Id, caret);
    }

    public void SetState(ItemRowViewModel row, ItemState state)
    {
        if (row.Item.State == state)
        {
            row.Refresh();
            return;
        }
        Guid? focus = null;
        // Finishing the focused item moves the cursor on to the next one, like ticking down a list.
        if (_focusedItemId == row.Item.Id && row.Parent == null && (state == ItemState.Done || row.Item.IsDone))
            focus = NextRowAfter(row)?.Item.Id ?? row.Item.Id;
        Mutate(() => row.Item.SetState(state), [row.Category], focus);
    }

    private ItemRowViewModel? NextRowAfter(ItemRowViewModel row)
    {
        var rows = VisibleRows();
        var i = rows.IndexOf(row);
        if (i < 0) return null;
        var j = i + 1;
        while (j < rows.Count && rows[j].Category == row.Category && rows[j].Depth > row.Depth && rows[j].InDoneSection == row.InDoneSection) j++;
        if (j < rows.Count) return rows[j];
        return i > 0 ? rows[i - 1] : null;
    }

    public void Delete(ItemRowViewModel row, bool focusPrevious)
    {
        var count = row.Item.Descendants().Count();
        if (count > 0 && MessageBox.Show(Application.Current.MainWindow!,
                $"Delete \"{Shorten(row.Item.Text)}\" and its {count} sub-item(s)?", "Delete item",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var rows = VisibleRows();
        var i = rows.IndexOf(row);
        var focus = focusPrevious ? (i > 0 ? rows[i - 1] : NextRowAfter(row)) : NextRowAfter(row);
        if (focus?.Item == row.Item) focus = null;
        Mutate(() => SiblingsOf(row).Remove(row.Item), [row.Category], focus?.Item.Id);
    }

    public void SetTag(ItemRowViewModel row, string? tag)
    {
        Checkpoint();
        row.Item.Tag = tag;
        Touch();
        row.Refresh();
        RefreshTagFilters();
        ApplyFilter();
    }

    public void MoveToCategory(ItemRowViewModel row, CategoryViewModel target)
    {
        if (target.Model == row.Category.Model && row.Parent == null) return;
        Mutate(() =>
        {
            SiblingsOf(row).Remove(row.Item);
            target.Model.Items.Add(row.Item);
        }, [row.Category, target]);
    }

    /// <summary>Drag &amp; drop: place <paramref name="source"/> before or after <paramref name="target"/> as its sibling.</summary>
    public void MoveItem(ItemRowViewModel source, ItemRowViewModel target, bool after)
    {
        if (source.Item == target.Item || source.Item.Descendants().Contains(target.Item) || SortedByPriority()) return;
        Mutate(() =>
        {
            SiblingsOf(source).Remove(source.Item);
            var list = SiblingsOf(target);
            list.Insert(list.IndexOf(target.Item) + (after ? 1 : 0), source.Item);
        }, [source.Category, target.Category]);
    }

    public void MoveCategory(CategoryViewModel category, int delta)
    {
        var index = Project.Categories.IndexOf(category.Model);
        var target = index + delta;
        if (target < 0 || target >= Project.Categories.Count) return;
        Checkpoint();
        Project.Categories.RemoveAt(index);
        Project.Categories.Insert(target, category.Model);
        Touch();
        RebuildAll();
    }

    public void MoveCategoryTo(CategoryViewModel category, CategoryViewModel target)
    {
        if (category == target) return;
        Checkpoint();
        Project.Categories.Remove(category.Model);
        Project.Categories.Insert(Project.Categories.IndexOf(target.Model), category.Model);
        Touch();
        RebuildAll();
    }

    [RelayCommand]
    private void AddCategory()
    {
        var name = InputDialog.Ask(Application.Current.MainWindow!, "New category", "Category name (ALL CAPS matches your TXT files):", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        Checkpoint();
        var category = new Category { Name = name.Trim() };
        Project.Categories.Add(category);
        Touch();
        RebuildAll();
        if (CategoryFor(category) is { } vm) FocusAddItemRequested?.Invoke(vm);
    }

    /// <summary>Adds the categories of another project that this one lacks (without items) as one undo step.</summary>
    public int CopyCategoriesFrom(Project source)
    {
        if (source.Categories.All(c => Project.FindCategory(c.Name) != null)) return 0;
        Checkpoint();
        var added = Project.CopyCategoriesFrom(source);
        Touch();
        RebuildAll();
        return added.Count;
    }

    public void DeleteCategory(CategoryViewModel category)
    {
        var count = category.Model.Walk().Count();
        if (count > 0 && MessageBox.Show(Application.Current.MainWindow!,
                $"Delete category \"{category.Name}\" with {count} item(s)?", "Delete category",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Checkpoint();
        Project.Categories.Remove(category.Model);
        Touch();
        RebuildAll();
    }

    public void RequestAddItem(CategoryViewModel category)
    {
        category.IsExpanded = true;
        FocusAddItemRequested?.Invoke(category);
    }

    public void PromoteToProject(ItemRowViewModel row)
    {
        var lines = row.Item.Text.Replace("\r\n", "\n").Split('\n');
        var name = lines[0].Trim();
        if (name.Length == 0) return;
        if (MessageBox.Show(Application.Current.MainWindow!,
                $"Create a new project \"{Shorten(name)}\" from this item? It will be removed from {Project.Name}.",
                "Promote to project", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var project = Core.Model.Project.Create(name, Project.IsInbox ? "Personal" : Project.Group);
        project.Status = ProjectStatus.Idea;
        project.Description = lines.Length > 1 ? string.Join("\n", lines.Skip(1)).Trim() : null;
        project.Categories.Add(new Category { Name = "GENERAL", Items = row.Item.Children.ToList() });
        Mutate(() => SiblingsOf(row).Remove(row.Item), [row.Category]);
        _state.AddProject(project);
        Main.OpenProject(project);
    }

    // ───────────── Selection & AI prompt ─────────────

    public IEnumerable<ItemRowViewModel> AllRows => Categories.SelectMany(c => c.AllRows);

    [RelayCommand]
    private void CopyPrompt()
    {
        var selected = AllRows.Where(r => r.IsSelected).Select(r => r.Item).Distinct().ToList();
        CopyPromptFor(selected.Count > 0 ? selected : Project.Categories.SelectMany(c => c.Items).Where(i => !i.IsDone));
    }

    public void CopyPromptFor(IEnumerable<TodoItem> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            _state.StatusText = "Nothing to copy — no open items.";
            return;
        }
        var text = PromptBuilder.Build(Project, list, _state.Store.AttachmentPath);
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _state.StatusText = "Clipboard is busy, try again.";
            return;
        }
        if (_state.Settings.MarkCopiedInProgress)
        {
            var todo = list.Where(i => i.State == ItemState.Todo).ToList();
            if (todo.Count > 0) Mutate(() => todo.ForEach(i => i.State = ItemState.InProgress), Categories);
        }
        _state.StatusText = $"Copied a prompt with {list.Count} item(s) to the clipboard.";
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in AllRows) row.IsSelected = false;
    }

    // ───────────── Attachments ─────────────

    public string AttachmentPath(Attachment attachment) => _state.Store.AttachmentPath(attachment);

    public void BrowseAttachments(ItemRowViewModel row)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Attach files", Multiselect = true };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) _ = AttachFiles(row, dialog.FileNames);
    }

    /// <summary>Copies the files into the data folder (off the UI thread, they may be large) and attaches them to the row's item.</summary>
    public async Task AttachFiles(ItemRowViewModel row, IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            _state.StatusText = "Only files can be attached, not folders.";
            return;
        }
        _state.StatusText = files.Count == 1 ? $"Attaching {Path.GetFileName(files[0])}…" : $"Attaching {files.Count} files…";
        List<Attachment> added;
        try
        {
            added = await Task.Run(() => files.Select(_state.Store.ImportAttachment).ToList());
        }
        catch (Exception ex)
        {
            _state.StatusText = "Could not attach: " + ex.Message;
            return;
        }
        AddAttachments(row.Item.Id, added);
    }

    /// <summary>Attaches what was copied to the clipboard: files, or an image such as a screenshot.</summary>
    public void PasteAttachments(ItemRowViewModel row)
    {
        var (files, png) = ClipboardAttachments.Read();
        if (files.Count > 0)
        {
            _ = AttachFiles(row, files);
            return;
        }
        if (png == null)
        {
            _state.StatusText = "The clipboard has no image or files to attach.";
            return;
        }
        try
        {
            AddAttachments(row.Item.Id, [_state.Store.WriteAttachment($"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}.png", png)]);
        }
        catch (Exception ex)
        {
            _state.StatusText = "Could not attach: " + ex.Message;
        }
    }

    private void AddAttachments(Guid itemId, List<Attachment> added)
    {
        // Found again by id: an undo while the files were copying replaces the item objects.
        if (Project.Find(itemId) is not { } loc)
        {
            _state.StatusText = "The item was removed before its files were attached.";
            return;
        }
        Mutate(() => (loc.Item.Attachments ??= []).AddRange(added), [CategoryFor(loc.Category)]);
        _state.StatusText = added.Count == 1 ? $"Attached {added[0].FileName}." : $"Attached {added.Count} files.";
    }

    public void RemoveAttachment(ItemRowViewModel row, Attachment attachment)
    {
        Mutate(() =>
        {
            row.Item.Attachments?.Remove(attachment);
            if (row.Item.Attachments is { Count: 0 }) row.Item.Attachments = null;
        }, [row.Category]);
        _state.StatusText = $"Removed {attachment.FileName} (Ctrl+Z brings it back).";
    }

    public void BrowseNoteAttachments(NoteViewModel note)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Attach files to the note", Multiselect = true };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) _ = AttachFilesToNote(note.Model, dialog.FileNames);
    }

    private async Task AttachFilesToNote(NoteDoc note, IReadOnlyList<string> paths)
    {
        List<Attachment> added;
        try
        {
            added = await Task.Run(() => paths.Where(File.Exists).Select(_state.Store.ImportAttachment).ToList());
        }
        catch (Exception ex)
        {
            _state.StatusText = "Could not attach: " + ex.Message;
            return;
        }
        if (added.Count == 0 || !Project.Notes.Contains(note)) return;
        Checkpoint();
        (note.Attachments ??= []).AddRange(added);
        Touch();
        Notes.FirstOrDefault(n => n.Model == note)?.Refresh();
        _state.StatusText = added.Count == 1 ? $"Attached {added[0].FileName}." : $"Attached {added.Count} files.";
    }

    public void RemoveNoteAttachment(NoteDoc note, Attachment attachment)
    {
        Checkpoint();
        note.Attachments?.Remove(attachment);
        if (note.Attachments is { Count: 0 }) note.Attachments = null;
        Touch();
        Notes.FirstOrDefault(n => n.Model == note)?.Refresh();
        _state.StatusText = $"Removed {attachment.FileName} (Ctrl+Z brings it back).";
    }

    // Programs and scripts are shown in their folder instead of being run by a stray click.
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".com", ".bat", ".cmd", ".msi", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".scr", ".lnk", ".url", ".reg", ".hta", ".cpl" };

    public void OpenAttachment(AttachmentViewModel attachment)
    {
        if (RunnableExtensions.Contains(Path.GetExtension(attachment.FileName)))
        {
            ShowAttachmentInFolder(attachment);
            return;
        }
        if (AttachmentExists(attachment)) Try(() => ShellLauncher.OpenFile(attachment.Path));
    }

    public void ShowAttachmentInFolder(AttachmentViewModel attachment)
    {
        if (AttachmentExists(attachment)) Try(() => ShellLauncher.ShowInFolder(attachment.Path));
    }

    private bool AttachmentExists(AttachmentViewModel attachment)
    {
        if (File.Exists(attachment.Path)) return true;
        _state.StatusText = $"{attachment.FileName} is missing from the data folder.";
        return false;
    }

    // ───────────── Review & agent edits ─────────────

    public void AcceptReview(ItemRowViewModel row)
    {
        if (!row.Item.NeedsReview) return;
        Mutate(() =>
        {
            row.Item.NeedsReview = false;
            row.Item.AddActivity(_state.UserName, "Accepted the work.", agent: false);
        }, [row.Category]);
        _state.StatusText = $"Accepted “{Shorten(row.Item.Text)}”.";
    }

    /// <summary>Frees an item an agent left checked out: back to do, so any agent can pick it up.</summary>
    public void ReleaseClaim(ItemRowViewModel row)
    {
        if (row.Item.ClaimedBy is not { } agent) return;
        Mutate(() =>
        {
            row.Item.SetState(ItemState.Todo);
            row.Item.AddActivity(_state.UserName, $"Released {agent}'s check-out.", agent: false);
        }, [row.Category]);
        _state.StatusText = $"Released “{Shorten(row.Item.Text)}”.";
    }

    /// <summary>
    /// A change from outside the board (an AI agent, the discussion panel): one undo step, the affected categories
    /// redrawn, and the cursor put back where it was if you were typing in the board (redrawn rows lose focus).
    /// </summary>
    private void MutateKeepingCursor(Action change, IEnumerable<CategoryViewModel?> affected, bool rebuildAll = false)
    {
        var focus = _focusedItemId;
        var caret = focus is { } focused ? CaretOf?.Invoke(focused) : null;
        Checkpoint();
        change();
        Touch();
        if (rebuildAll) RebuildAll();
        else foreach (var c in affected.OfType<CategoryViewModel>().Distinct()) c.Refresh();
        RefreshTagFilters();
        ApplyFilter();
        if (focus is { } id && caret is { } at && Project.Find(id) != null) FocusRequested?.Invoke(id, at);
    }

    public void AgentEdit(Guid itemId, Action<TodoItem> change)
    {
        var loc = Project.Find(itemId) ?? throw new InvalidOperationException("The item is no longer in this project.");
        MutateKeepingCursor(() => change(loc.Item), [CategoryFor(loc.Category)]);
    }

    /// <summary>Adds an item under a parent item, or at the end of a category (created when missing).</summary>
    public void AgentAdd(TodoItem item, string? categoryName, Guid? parentId)
    {
        if (parentId is { } pid)
        {
            var parent = Project.Find(pid) ?? throw new InvalidOperationException("The parent item is not in this project.");
            MutateKeepingCursor(() => parent.Item.Children.Add(item), [CategoryFor(parent.Category)]);
            return;
        }
        var category = Project.FindCategory(categoryName ?? "");
        if (category != null)
        {
            MutateKeepingCursor(() => category.Items.Add(item), [CategoryFor(category)]);
            return;
        }
        MutateKeepingCursor(() => Project.Categories.Add(new Category { Name = categoryName!.Trim(), Items = [item] }), [], rebuildAll: true);
    }

    // ───────────── Discussions ─────────────

    /// <summary>Opens the discussion of an item or category of this project in the side panel.</summary>
    public void OpenDiscussion(Guid targetId) => Main.OpenDiscussion(DiscussionTarget.InProject(Project, targetId));

    /// <summary>The threads of an item or a category of this project; null when it has none or is gone.</summary>
    public List<Comment>? CommentsOf(Guid targetId) =>
        Project.Find(targetId)?.Item.Comments ?? Project.Categories.FirstOrDefault(c => c.Id == targetId)?.Comments;

    public bool HasDiscussionTarget(Guid targetId) => Project.Find(targetId) != null || Project.Categories.Any(c => c.Id == targetId);

    /// <summary>Posts on an item or category (a new thread or a reply); one undo step.</summary>
    public void PostComment(Guid targetId, Comment comment, Guid? replyTo)
    {
        if (replyTo is { } parent && CommentsOf(targetId).Find(parent) == null)
            throw new InvalidOperationException("The comment to reply to isn't there any more.");
        if (Project.Find(targetId) is { } loc)
            MutateKeepingCursor(() => loc.Item.Comments = Discussions.Post(loc.Item.Comments, comment, replyTo), [CategoryFor(loc.Category)]);
        else if (Project.Categories.FirstOrDefault(c => c.Id == targetId) is { } category)
            MutateKeepingCursor(() => category.Comments = Discussions.Post(category.Comments, comment, replyTo), [CategoryFor(category)]);
        else
            throw new InvalidOperationException("The item or category isn't there any more.");
    }

    public void RemoveComment(Guid targetId, Guid commentId)
    {
        if (Project.Find(targetId) is { } loc)
            MutateKeepingCursor(() => loc.Item.Comments = Discussions.Remove(loc.Item.Comments, commentId), [CategoryFor(loc.Category)]);
        else if (Project.Categories.FirstOrDefault(c => c.Id == targetId) is { } category)
            MutateKeepingCursor(() => category.Comments = Discussions.Remove(category.Comments, commentId), [CategoryFor(category)]);
    }

    // ───────────── Worked on by ─────────────

    /// <summary>Shows only items this person or agent worked on (history or discussion); null shows everyone's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkedByActive))]
    private string? _workedBy;

    partial void OnWorkedByChanged(string? value) => ApplyFilter();

    public bool IsWorkedByActive => WorkedBy != null;

    /// <summary>Everyone who worked on something in this project, for the filter bar's picker.</summary>
    public IReadOnlyList<string> WorkedByChoices => Project.Walk().SelectMany(l => l.Item.Contributors()).Select(c => c.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    [RelayCommand]
    private void ShowWorkedBy(string? name)
    {
        OnPropertyChanged(nameof(WorkedByChoices));
        WorkedBy = string.IsNullOrWhiteSpace(name) ? null : name;
    }

    [RelayCommand] private void ClearWorkedBy() => WorkedBy = null;

    /// <summary>Creates the note with this title or replaces (or appends to) its text.</summary>
    public NoteDoc AgentWriteDoc(string title, string text, bool append, List<Attachment> files, string by, out bool created)
    {
        var note = Project.Notes.FirstOrDefault(n => string.Equals(n.Title.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
        created = note == null;
        var selected = SelectedNote?.Model;
        Checkpoint();
        if (note == null)
        {
            note = new NoteDoc { Title = title.Trim() };
            Project.Notes.Add(note);
        }
        note.Text = append && note.Text.Trim().Length > 0 ? note.Text.TrimEnd() + "\n\n" + text : text;
        note.UpdatedAt = DateTime.Now;
        note.UpdatedBy = by;
        if (files.Count > 0) (note.Attachments ??= []).AddRange(files);
        Touch();
        ReloadSideLists();
        SelectedNote = Notes.FirstOrDefault(n => n.Model == (selected ?? note)) ?? Notes.FirstOrDefault();
        return note;
    }

    public Release AgentSaveRelease(string version, string? title, List<PatchNoteEntry> entries, string markdown, string by)
    {
        Checkpoint();
        var release = PatchNotesBuilder.CreateRelease(Project, version, DateTime.Now, title, entries, markdown);
        release.CreatedBy = by;
        Touch();
        ReloadSideLists();
        RebuildAll();
        return release;
    }

    // ───────────── Repo links ─────────────

    private bool RepoExists()
    {
        if (!string.IsNullOrWhiteSpace(Project.RepoPath) && Directory.Exists(Project.RepoPath)) return true;
        _state.StatusText = "Set an existing local repo folder in the Details tab first.";
        SelectedTab = 4;
        return false;
    }

    [RelayCommand] private void OpenFolder() { if (RepoExists()) ShellLauncher.OpenFolder(Project.RepoPath!); }
    [RelayCommand] private void OpenVsCode() { if (RepoExists()) Try(() => ShellLauncher.OpenInVsCode(Project.RepoPath!, _state.Settings.VsCodePath)); }
    [RelayCommand] private void OpenTerminal() { if (RepoExists()) Try(() => ShellLauncher.OpenTerminal(Project.RepoPath!)); }

    [RelayCommand]
    private void OpenGitHub()
    {
        if (string.IsNullOrWhiteSpace(Project.GitHubUrl))
        {
            _state.StatusText = "Set the GitHub URL in the Details tab first.";
            SelectedTab = 4;
            return;
        }
        Try(() => ShellLauncher.OpenUrl(Project.GitHubUrl));
    }

    [RelayCommand]
    private void BrowseRepo()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Local repository folder" };
        if (!string.IsNullOrWhiteSpace(RepoPath) && Directory.Exists(RepoPath)) dialog.InitialDirectory = RepoPath;
        if (dialog.ShowDialog() == true) RepoPath = dialog.FolderName;
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { _state.StatusText = ex.Message; }
    }

    [RelayCommand]
    private void Export() => new ExportDialog(_state, Project) { Owner = Application.Current.MainWindow }.ShowDialog();

    // ───────────── Notes, releases, archive ─────────────

    [ObservableProperty] private NoteViewModel? _selectedNote;
    [ObservableProperty] private Release? _selectedRelease;
    [ObservableProperty] private ArchivedFile? _selectedArchive;

    private void ReloadSideLists()
    {
        Notes.Clear();
        foreach (var note in Project.Notes) Notes.Add(new NoteViewModel(this, note));
        SelectedNote = Notes.FirstOrDefault();
        Releases.Clear();
        foreach (var release in Project.Releases.OrderByDescending(r => r.Date)) Releases.Add(release);
        SelectedRelease = Releases.FirstOrDefault();
        Archive.Clear();
        foreach (var file in Project.ArchivedFiles) Archive.Add(file);
        SelectedArchive = Archive.FirstOrDefault();
    }

    [RelayCommand]
    private void AddNote()
    {
        var note = new NoteDoc { Title = "New note" };
        Project.Notes.Add(note);
        var vm = new NoteViewModel(this, note);
        Notes.Add(vm);
        SelectedNote = vm;
        Touch();
    }

    [RelayCommand]
    private void DeleteNote()
    {
        if (SelectedNote is not { } note) return;
        if (MessageBox.Show(Application.Current.MainWindow!, $"Delete note \"{note.Title}\"?", "Delete note",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Checkpoint();
        Project.Notes.Remove(note.Model);
        Notes.Remove(note);
        SelectedNote = Notes.FirstOrDefault();
        Touch();
    }

    [RelayCommand]
    private void NewRelease()
    {
        var window = new PatchNotesWindow(new PatchNotesViewModel(_state, this)) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        ReloadSideLists();
        RebuildAll();
        SelectedTab = 2;
    }

    [RelayCommand]
    private void CopyRelease()
    {
        if (SelectedRelease == null) return;
        try
        {
            Clipboard.SetText(SelectedRelease.Markdown);
            _state.StatusText = $"Copied patch notes for {SelectedRelease.Version}.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _state.StatusText = "Clipboard is busy, try again.";
        }
    }

    [RelayCommand]
    private void DeleteRelease()
    {
        if (SelectedRelease is not { } release) return;
        if (MessageBox.Show(Application.Current.MainWindow!,
                $"Delete release \"{release.Version}\"? Its items become unreleased again and will show up in the next patch notes.",
                "Delete release", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Checkpoint();
        foreach (var loc in Project.Walk().Where(l => l.Item.ReleaseId == release.Id)) loc.Item.ReleaseId = null;
        Project.Releases.Remove(release);
        Touch();
        ReloadSideLists();
        RebuildAll();
    }

    // ───────────── Navigation helpers ─────────────

    public void FocusRelative(ItemRowViewModel row, int delta)
    {
        var rows = VisibleRows();
        var i = rows.IndexOf(row);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= rows.Count) return;
        FocusRequested?.Invoke(rows[j].Item.Id, -1);
    }

    /// <summary>Clears the filter, expands the item's category (and DONE section) and focuses it.</summary>
    public void Reveal(TodoItem item)
    {
        SelectedTab = 0;
        FilterText = "";
        TagFilter = AllTags;
        StateFilter = StateFilters[0];
        PriorityFilter = PriorityFilters[0];
        WorkedBy = null;
        if (Project.Find(item.Id) is not { } loc || CategoryFor(loc.Category) is not { } category) return;
        category.IsExpanded = true;
        if (loc.Item.IsDone || Project.Walk().Any(l => l.Depth == 0 && l.Item.IsDone && l.Item.Descendants().Contains(item)))
            category.IsDoneExpanded = true;
        Application.Current.Dispatcher.BeginInvoke(() => FocusRequested?.Invoke(item.Id, -1),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    public void RevealNote(NoteDoc note)
    {
        SelectedTab = 1;
        SelectedNote = Notes.FirstOrDefault(n => n.Model == note);
    }

    private static string Shorten(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')[0];
        return line.Length > 60 ? line[..60] + "…" : line;
    }
}

/// <summary>A choice in the board's priority menu: items at least as pressing as <paramref name="AtLeast"/>, or only those without a priority.</summary>
public sealed record PriorityFilterOption(string Label, Priority? AtLeast, bool NoneOnly = false)
{
    public bool IsActive => AtLeast != null || NoneOnly;
    public bool Matches(TodoItem item) => NoneOnly ? item.Priority == Priority.None : AtLeast is not { } min || item.Priority >= min;
    public override string ToString() => Label;
}

/// <summary>A choice in the board's state filter; State is null for "All states".</summary>
public sealed record StateFilterOption(string Label, ItemState? State, bool ReviewOnly = false)
{
    public override string ToString() => Label;
}

public sealed partial class NoteViewModel(ProjectViewModel owner, NoteDoc model) : ObservableObject
{
    private IReadOnlyList<AttachmentViewModel>? _attachments;

    public NoteDoc Model { get; } = model;

    public IReadOnlyList<AttachmentViewModel> Attachments => _attachments ??=
        (Model.Attachments ?? []).Select(a => new AttachmentViewModel(owner, a, () => owner.RemoveNoteAttachment(Model, a))).ToList();
    public bool HasAttachments => Model.Attachments is { Count: > 0 };

    public void Refresh()
    {
        _attachments = null;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand] private void Attach() => owner.BrowseNoteAttachments(this);

    /// <summary>"Last changed 14:02 by Claude Code"; empty for notes from before notes were signed.</summary>
    public string? EditedText => Model.UpdatedBy is { } by
        ? $"Last changed {Model.UpdatedAt.ToString(Model.UpdatedAt.Date == DateTime.Today ? "HH:mm" : "d MMM yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture)} by {by}"
        : null;

    public string Title
    {
        get => Model.Title;
        set
        {
            if (value == Model.Title) return;
            Model.Title = value;
            Signed();
            OnPropertyChanged();
        }
    }

    public string Text
    {
        get => Model.Text;
        set
        {
            if (value == Model.Text) return;
            Model.Text = value;
            Signed();
            OnPropertyChanged();
        }
    }

    private void Signed()
    {
        Model.UpdatedAt = DateTime.Now;
        Model.UpdatedBy = owner.UserName;
        owner.Touch();
        OnPropertyChanged(nameof(EditedText));
    }
}
