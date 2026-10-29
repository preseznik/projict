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

    private IReadOnlyList<AttachmentViewModel>? _attachments;
    public IReadOnlyList<AttachmentViewModel> Attachments => _attachments ??=
        (Item.Attachments ?? []).Select(a => new AttachmentViewModel(Owner, a, () => Owner.RemoveAttachment(this, a))).ToList();
    public bool HasAttachments => Item.HasAttachments;

    /// <summary>The agent working on the item, shown as a chip while it is in progress.</summary>
    public string? ClaimedBy => Item.State == ItemState.InProgress ? Item.ClaimedBy : null;
    public bool NeedsReview => Item.NeedsReview;
    public string ClaimToolTip => $"{Item.ClaimedBy} has this checked out: other agents can't change it. Right-click › Release check-out to free it.";
    public string ReviewToolTip => $"Finished by {Item.Activity?.LastOrDefault(a => a.Agent)?.By ?? "an AI agent"} — click to accept once you have checked it. " +
                                   "Mark it to do or in progress to send it back.";

    /// <summary>The newest activity entry, e.g. an agent's resolution, as one line under the text.</summary>
    public string? ActivityText => Item.Activity?.LastOrDefault() is { } last
        ? $"{last.By} · {When(last.At)} · {last.Text.Replace("\r\n", " ").Replace('\n', ' ')}"
        : null;
    public string? ActivityToolTip => Item.Activity is { Count: > 0 } log
        ? string.Join("\n\n", log.Select(a => $"{a.At.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture)} · {a.By}\n{a.Text}"))
        : null;

    /// <summary>Everyone (agents and people) who wrote in the item's history or discussion; a click filters the board by them.</summary>
    public IReadOnlyList<ContributorViewModel> Contributors => Item.Contributors()
        .Select(c => new ContributorViewModel(c, ContributorToolTip(c.Name), Owner.ShowWorkedByCommand)).ToList();
    public int CommentCount => Item.Comments.CountComments();
    /// <summary>The second line shows when there is something on it.</summary>
    public bool HasDetails => Item.Activity is { Count: > 0 } || CommentCount > 0;

    private string ContributorToolTip(string name)
    {
        var lines = (Item.Activity ?? []).Where(a => string.Equals(a.By, name, StringComparison.OrdinalIgnoreCase))
            .Select(a => $"{a.At.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)}  {a.Text}").ToList();
        var comments = Item.Comments.Walk().Count(e => !e.Comment.Deleted && string.Equals(e.Comment.By, name, StringComparison.OrdinalIgnoreCase));
        if (comments > 0) lines.Add(comments == 1 ? "1 comment" : $"{comments} comments");
        lines.Add("Click to show only what " + name + " worked on");
        return name + "\n" + string.Join("\n", lines);
    }

    private static string When(DateTime at) => at.Date == DateTime.Today
        ? at.ToString("HH:mm", CultureInfo.CurrentCulture)
        : at.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);

    public string? ReleaseText => Item.ReleaseId is { } id
        ? Owner.Project.Releases.FirstOrDefault(r => r.Id == id) is { IsBaseline: false } r ? r.Version : null
        : null;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isDropTarget;

    public void Refresh()
    {
        _attachments = null;
        OnPropertyChanged(string.Empty);
    }

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
    [RelayCommand] private void Attach() => Owner.BrowseAttachments(this);
    [RelayCommand] private void PasteAttachments() => Owner.PasteAttachments(this);
    [RelayCommand] private void AcceptReview() => Owner.AcceptReview(this);
    [RelayCommand] private void ReleaseClaim() => Owner.ReleaseClaim(this);
    [RelayCommand] private void OpenDiscussion() => Owner.OpenDiscussion(Item.Id);
}

/// <summary>A chip under an item naming someone who worked on it.</summary>
public sealed record ContributorViewModel(Contributor Contributor, string ToolTip, System.Windows.Input.ICommand Filter)
{
    public string Name => Contributor.Name;
    /// <summary>A robot for AI agents, a person otherwise.</summary>
    public string Glyph => Contributor.Agent ? "" : "";
}
