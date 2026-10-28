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
    public IReadOnlyList<string> Groups => _state.Settings.Groups;
    public IReadOnlyList<string> TagFilters { get; private set; } = [AllTags];
    public bool IsInbox => Project.IsInbox;
    public bool IsNotInbox => !Project.IsInbox;

    /// <summary>Asks the view to focus an item's text box; caret -1 means end of text.</summary>
    public event Action<Guid, int>? FocusRequested;
    public event Action<CategoryViewModel>? FocusAddItemRequested;

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
            SetDetail(Project.Group, value, v => Project.Group = v?.Trim() ?? "", listChanged: true);
            if (!string.IsNullOrWhiteSpace(value) && !_state.Settings.Groups.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                _state.Settings.Groups.Add(value.Trim());
                _state.MarkSettingsDirty();
            }
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

    private void NotifyDetails()
    {
        foreach (var p in new[] { nameof(Name), nameof(Key), nameof(FilePrefix), nameof(Group), nameof(Status), nameof(RepoPath), nameof(GitHubUrl), nameof(PromptPreamble), nameof(Description) })
            OnPropertyChanged(p);
    }

    // ───────────── Board state ─────────────

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _tagFilter = AllTags;
    [ObservableProperty] private bool _showDone;
    [ObservableProperty] private bool _sideBySide;
    [ObservableProperty] private int _selectedTab;

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnTagFilterChanged(string value) => ApplyFilter();

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

    public bool IsFilterActive => !string.IsNullOrWhiteSpace(FilterText) || (TagFilter ?? AllTags) != AllTags;

    public void Touch() => _state.MarkDirty(Project);

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

        bool Matches(TodoItem item)
        {
            var tagOk = tag == AllTags || (tag == NoTag ? string.IsNullOrEmpty(item.Tag) : string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase));
            var text = item.DisplayText();
            return tagOk && terms.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
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

            // A match keeps its ancestors and descendants visible so the tree still reads correctly.
            var parents = new Dictionary<TodoItem, TodoItem>();
            var visible = new HashSet<TodoItem>();
            foreach (var loc in category.Model.Walk())
            {
                if (loc.Parent != null) parents[loc.Item] = loc.Parent;
                if (!Matches(loc.Item)) continue;
                visible.Add(loc.Item);
                foreach (var d in loc.Item.Descendants()) visible.Add(d);
                for (var p = loc.Parent; p != null; p = parents.GetValueOrDefault(p)) visible.Add(p);
            }
            var nameMatches = terms.Length > 0 && tag == AllTags && terms.All(t => category.Name.Contains(t, StringComparison.OrdinalIgnoreCase));
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
        var item = new TodoItem();
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
        if (source.Item == target.Item || source.Item.Descendants().Contains(target.Item)) return;
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
        var text = PromptBuilder.Build(Project, list);
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

public sealed class NoteViewModel(ProjectViewModel owner, NoteDoc model) : ObservableObject
{
    public NoteDoc Model { get; } = model;

    public string Title
    {
        get => Model.Title;
        set
        {
            if (value == Model.Title) return;
            Model.Title = value;
            Model.UpdatedAt = DateTime.Now;
            owner.Touch();
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
            Model.UpdatedAt = DateTime.Now;
            owner.Touch();
            OnPropertyChanged();
        }
    }
}
