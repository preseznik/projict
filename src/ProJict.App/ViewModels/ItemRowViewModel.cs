using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>One line on the board: an item at some depth, shown in its category's TODO or DONE section.</summary>
public sealed partial class ItemRowViewModel : ObservableObject
{
    public ItemRowViewModel(CategoryViewModel category, TodoItem item, TodoItem? parent, int depth, bool inDoneSection)
    {
        Category = category;
        Item = item;
        Parent = parent;
        Depth = depth;
        InDoneSection = inDoneSection;
    }

    public TodoItem Item { get; }
    public CategoryViewModel Category { get; }
    public TodoItem? Parent { get; }
    public int Depth { get; }
    public bool InDoneSection { get; }
    public ProjectViewModel Owner => Category.Owner;

    public Thickness IndentMargin => new(Depth * 26, 0, 0, 0);

    public string Text
    {
        get => Item.Text;
        set
        {
            if (value == Item.Text) return;
            Owner.BeforeTextChange();
            Item.Text = value;
            Owner.Touch();
            OnPropertyChanged();
        }
    }

    public string? Tag => Item.Tag;
    public bool HasTag => !string.IsNullOrWhiteSpace(Item.Tag);
    public bool IsDone => Item.IsDone;
    public bool IsInProgress => Item.State == ItemState.InProgress;
    /// <summary>A done child still listed under an open parent (shown struck through).</summary>
    public bool IsDoneInOpenSection => Item.IsDone && !InDoneSection;

    /// <summary>The two states the item isn't in, offered when its checkbox is right-clicked.</summary>
    public IEnumerable<ItemState> OtherStates => Enum.GetValues<ItemState>().Where(s => s != Item.State);

    /// <summary>Three-state checkbox: unchecked = todo, indeterminate = in progress, checked = done.</summary>
    public bool? CheckState
    {
        get => Item.State switch { ItemState.Done => true, ItemState.InProgress => null, _ => false };
        set => Owner.SetState(this, value == true ? ItemState.Done : ItemState.Todo);
    }

    public string? DoneAtText => !Item.IsDone ? null
        : Item.Imported ? "imported"
        : Item.DoneAt?.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public string? ReleaseText => Item.ReleaseId is { } id
        ? Owner.Project.Releases.FirstOrDefault(r => r.Id == id) is { IsBaseline: false } r ? r.Version : null
        : null;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isDropTarget;

    public void Refresh() => OnPropertyChanged(string.Empty);

    public override string ToString() => Item.DisplayText();

    [RelayCommand] private void ToggleDone() => Owner.SetState(this, Item.IsDone ? ItemState.Todo : ItemState.Done);
    [RelayCommand] private void ToggleInProgress() => Owner.SetState(this, Item.State == ItemState.InProgress ? ItemState.Todo : ItemState.InProgress);
    [RelayCommand] private void SetTag(string? tag) => Owner.SetTag(this, string.IsNullOrWhiteSpace(tag) ? null : tag);
    [RelayCommand] private void MoveToCategory(CategoryViewModel? target)
    {
        if (target != null) Owner.MoveToCategory(this, target);
    }
    [RelayCommand] private void Delete() => Owner.Delete(this, focusPrevious: false);
    [RelayCommand] private void Promote() => Owner.PromoteToProject(this);
    [RelayCommand] private void CopyPrompt() => Owner.CopyPromptFor([Item]);
    [RelayCommand] private void ToggleSelected() => IsSelected = !IsSelected;
    [RelayCommand] private void AddChild() => Owner.AddChild(this);
}
