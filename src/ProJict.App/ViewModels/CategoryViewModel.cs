using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>A category with its TODO rows and, below or beside them, its DONE rows.</summary>
public sealed partial class CategoryViewModel : ObservableObject
{
    public CategoryViewModel(ProjectViewModel owner, Category model)
    {
        Owner = owner;
        Model = model;
        Refresh();
    }

    public ProjectViewModel Owner { get; }
    public Category Model { get; }

    public IReadOnlyList<ItemRowViewModel> OpenRows { get; private set; } = [];
    public IReadOnlyList<ItemRowViewModel> DoneRows { get; private set; } = [];

    public string Name
    {
        get => Model.Name;
        set
        {
            if (value == Model.Name) return;
            Owner.BeforeTextChange();
            Model.Name = value;
            Owner.Touch();
            OnPropertyChanged();
        }
    }

    public string? Description
    {
        get => Model.Description;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (v == Model.Description) return;
            Owner.BeforeTextChange();
            Model.Description = v;
            Owner.Touch();
            OnPropertyChanged();
        }
    }

    public bool IsExpanded
    {
        get => !Model.Collapsed;
        set
        {
            if (value == !Model.Collapsed) return;
            Model.Collapsed = !value;
            Owner.Touch();
            OnPropertyChanged();
            Owner.OnCategoryExpandedChanged();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDoneRows))]
    private bool _isDoneExpanded;

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isDropTarget;
    [ObservableProperty] private string _newItemText = "";

    public int OpenCount => Model.Items.Count(i => !i.IsDone);
    public int DoneCount => Model.Items.Count(i => i.IsDone);
    public string CountsText => DoneCount == 0 ? $"{OpenCount} open" : $"{OpenCount} open · {DoneCount} done";
    public string DoneHeader => $"Done ({DoneCount})";
    public bool HasDone => DoneCount > 0;

    /// <summary>Done rows show when expanded, in side-by-side layout, or when a filter matches some of them.</summary>
    public bool ShowDoneRows => IsDoneExpanded || Owner.SideBySide || (Owner.IsFilterActive && DoneRows.Any(r => r.IsVisible));

    public void Refresh()
    {
        var open = new List<ItemRowViewModel>();
        foreach (var item in Ordered(Model.Items.Where(i => !i.IsDone), false)) AddRows(open, item, null, 0, false);
        var done = new List<ItemRowViewModel>();
        foreach (var item in Model.Items.Where(i => i.IsDone).OrderByDescending(i => i.DoneAt ?? DateTime.MinValue))
            AddRows(done, item, null, 0, true);

        OpenRows = open;
        DoneRows = done;
        OnPropertyChanged(nameof(OpenRows));
        OnPropertyChanged(nameof(DoneRows));
        RefreshCounts();
    }

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(CountsText));
        OnPropertyChanged(nameof(DoneHeader));
        OnPropertyChanged(nameof(HasDone));
        OnPropertyChanged(nameof(ShowDoneRows));
        OnPropertyChanged(nameof(CommentCount));
        OnPropertyChanged(nameof(DiscussionToolTip));
    }

    public int CommentCount => Model.Comments.CountComments();
    public string DiscussionToolTip => CommentCount switch
    {
        0 => "Discuss this category",
        1 => "Discussion: 1 comment",
        var n => $"Discussion: {n} comments",
    };

    public void RaiseShowDone() => OnPropertyChanged(nameof(ShowDoneRows));

    private void AddRows(List<ItemRowViewModel> rows, TodoItem item, TodoItem? parent, int depth, bool done)
    {
        rows.Add(new ItemRowViewModel(this, item, parent, depth, done));
        foreach (var child in Ordered(item.Children, done)) AddRows(rows, child, item, depth + 1, done);
    }

    /// <summary>Open items most pressing first when the board is sorted by priority; otherwise (and in DONE) their own order.</summary>
    private IEnumerable<TodoItem> Ordered(IEnumerable<TodoItem> items, bool done) =>
        Owner.SortByPriority && !done ? items.ByPriority() : items;

    public IEnumerable<ItemRowViewModel> AllRows => OpenRows.Concat(DoneRows);

    public override string ToString() => Name;

    [RelayCommand]
    private void AddNewItem()
    {
        if (string.IsNullOrWhiteSpace(NewItemText)) return;
        Owner.AddItem(this, NewItemText);
        NewItemText = "";
    }

    [RelayCommand] private void CopyPrompt() => Owner.CopyPromptFor(Model.Items.Where(i => !i.IsDone));
    [RelayCommand] private void OpenDiscussion() => Owner.OpenDiscussion(Model.Id);
    [RelayCommand] private void MoveUp() => Owner.MoveCategory(this, -1);
    [RelayCommand] private void MoveDown() => Owner.MoveCategory(this, +1);
    [RelayCommand] private void Delete() => Owner.DeleteCategory(this);
    [RelayCommand] private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
